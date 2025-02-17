﻿// SPDX-FileCopyrightText: 2020 Frans van Dorsselaer
//
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Devices.Usb;

using static UsbIpServer.Interop.Linux;
using static UsbIpServer.Interop.UsbIp;
using static UsbIpServer.Interop.VBoxUsb;
using static UsbIpServer.Tools;

namespace UsbIpServer
{
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by DI")]
    sealed class AttachedClient
    {
        public AttachedClient(ILogger<AttachedClient> logger, ClientContext clientContext)
        {
            Logger = logger;
            ClientContext = clientContext;

            var tcpClient = clientContext.TcpClient;
            Stream = tcpClient.GetStream();

            Device = clientContext.AttachedDevice ?? throw new ArgumentException($"{nameof(ClientContext.AttachedDevice)} is null");
            ConfigurationDescriptors = clientContext.ConfigurationDescriptors ?? throw new ArgumentException($"{nameof(ClientContext.ConfigurationDescriptors)} is null");

            tcpClient.NoDelay = true;
        }

        readonly ILogger Logger;
        readonly ClientContext ClientContext;
        readonly NetworkStream Stream;
        readonly DeviceFile Device;
        readonly UsbConfigurationDescriptors ConfigurationDescriptors;
        readonly SemaphoreSlim WriteMutex = new(1);
        readonly object PendingSubmitsLock = new();
        /// <summary>
        /// Mapping from USBIP seqnum to raw USB endpoint number.
        /// </summary>
        readonly SortedDictionary<uint, byte> PendingSubmits = new();

        async Task HandleSubmitIsochronousAsync(UsbIpHeaderBasic basic, UsbIpHeaderCmdSubmit submit, CancellationToken cancellationToken)
        {
            var buf = new byte[submit.transfer_buffer_length];
            if (basic.direction == UsbIpDir.USBIP_DIR_OUT)
            {
                await Stream.ReadExactlyAsync(buf, cancellationToken);
            }

            var packetDescriptors = await Stream.ReadUsbIpIsoPacketDescriptorsAsync(submit.number_of_packets, cancellationToken);
            if (packetDescriptors.Any((d) => d.length > ushort.MaxValue))
            {
                // VBoxUSB uses ushort for length, and that is fine as none of the current
                // USB standards support larger ISO packets sizes. This is just a sanity check.
                throw new ProtocolViolationException("ISO packet too big");
            }
            if (packetDescriptors.Sum((d) => d.length) != submit.transfer_buffer_length)
            {
                // USBIP requires the packets in the data buffer to be sequential without any padding.
                throw new ProtocolViolationException($"cumulative lengths of ISO packets does not match transfer_buffer_length");
            }

            // Everything has been read and validated, now process...

            lock (PendingSubmitsLock)
            {
                // To support UNLINK, we must be able to abort the pipe that is used for this URB.
                // We need the raw USB endpoint number, i.e. including the high bit for input pipes.
                if (!PendingSubmits.TryAdd(basic.seqnum, (byte)(basic.ep | (basic.direction == UsbIpDir.USBIP_DIR_IN ? 0x80u : 0x00u))))
                {
                    throw new ProtocolViolationException($"duplicate sequence number {basic.seqnum}");
                }
                Logger.LogTrace($"Scheduled seqnum={basic.seqnum}, pending count = {PendingSubmits.Count}");
            }

            // VBoxUSB only excepts up to 8 iso packets per ioctl, so we may have to split
            // the request into multiple ioctls.
            List<Task> ioctls = new();

            // Input or output, single or multiple URBs, exceptions or not, this buffer must be locked until after all ioctls have completed.
            var gcHandle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                // Now queue as many ioctls as required, each ioctl covering as many iso packets as will fit:
                // up to 8 ISO packets per URB, or less if the offset does not fit into an ushort anymore.
                var isoIndex = 0;
                var urbBufOffset = 0;
                while (isoIndex < submit.number_of_packets)
                {
                    var urbIsoOffset = isoIndex;
                    var urb = new UsbSupUrb()
                    {
                        ep = basic.ep,
                        type = UsbSupTransferType.USBSUP_TRANSFER_TYPE_ISOC,
                        dir = (basic.direction == UsbIpDir.USBIP_DIR_IN) ? UsbSupDirection.USBSUP_DIRECTION_IN : UsbSupDirection.USBSUP_DIRECTION_OUT,
                        flags = UsbSupXferFlags.USBSUP_FLAG_NONE,
                        error = UsbSupError.USBSUP_XFER_OK,
                        len = 0,
                        buf = gcHandle.AddrOfPinnedObject() + urbBufOffset,
                        numIsoPkts = 0,
                        aIsoPkts = new UsbSupIsoPkt[8],
                    };

                    while (isoIndex < submit.number_of_packets // there are more iso packets in the original request
                        && urb.numIsoPkts < urb.aIsoPkts.Length // and more will actually fit in this URB
                        && urb.len <= ushort.MaxValue) // and the next URB-relative offset will fit in ushort
                    {
                        urb.aIsoPkts[urb.numIsoPkts].cb = (ushort)packetDescriptors[isoIndex].length;
                        urb.aIsoPkts[urb.numIsoPkts].off = (ushort)urb.len;
                        urb.len += urb.aIsoPkts[urb.numIsoPkts].cb;
                        urb.numIsoPkts++;
                        isoIndex++;
                    }

                    // No more iso packets will fit in this ioctl, or this was all of them, but we do have at least one.
                    var bytes = new byte[Marshal.SizeOf<UsbSupUrb>()];
                    StructToBytes(urb, bytes);
                    // Note that we are adding the continuation task, not the actual ioctl.
                    ioctls.Add(Device.IoControlAsync(SUPUSB_IOCTL.SEND_URB, bytes, bytes).ContinueWith((task, state) =>
                    {
                        BytesToStruct(bytes, out urb);

                        for (var i = 0; i < urb.numIsoPkts; ++i)
                        {
                            packetDescriptors[urbIsoOffset + i].actual_length = urb.aIsoPkts[i].cb;
                            packetDescriptors[urbIsoOffset + i].status = (uint)-(int)ConvertError(urb.aIsoPkts[i].stat);
                        }
                    }, cancellationToken, TaskScheduler.Default));

                    urbBufOffset += (int)urb.len;
                }

                // Continue when all ioctls *and* their continuations have been completed.
                _ = Task.WhenAll(ioctls).ContinueWith(async (task, state) =>
                {
                    using var writeLock = await Lock.CreateAsync(WriteMutex, cancellationToken);

                    // Now we are synchronous with the sender.

                    lock (PendingSubmitsLock)
                    {
                        // We are racing with possible UNLINK commands.
                        if (!PendingSubmits.Remove(basic.seqnum))
                        {
                            // Apparently, the client has already UNLINK-ed (canceled) the request; we're done.
                            Logger.LogTrace($"Completed seqnum={basic.seqnum} after UNLINK, pending count = {PendingSubmits.Count}");
                            return;
                        }
                    }

                    var header = new UsbIpHeader
                    {
                        basic = new UsbIpHeaderBasic
                        {
                            command = UsbIpCmd.USBIP_RET_SUBMIT,
                            seqnum = basic.seqnum,
                        },
                        ret_submit = new UsbIpHeaderRetSubmit()
                        {
                            status = -(int)Errno.SUCCESS,
                            actual_length = (int)packetDescriptors.Sum((pd) => pd.actual_length),
                            start_frame = submit.start_frame,
                            number_of_packets = submit.number_of_packets,
                            error_count = packetDescriptors.Count((d) => d.status != -(int)Errno.SUCCESS),
                        },
                    };

                    Logger.LogTrace($"ISO: error_count: {header.ret_submit.error_count}, actual_length: {header.ret_submit.actual_length}");

                    var retBuf = buf;
                    if ((basic.direction == UsbIpDir.USBIP_DIR_IN) && (header.ret_submit.actual_length != submit.transfer_buffer_length))
                    {
                        // USBIP requires us to transfer the actual data without padding.
                        retBuf = new byte[header.ret_submit.actual_length];
                        var sourceOffset = 0;
                        var destinationOffset = 0;
                        foreach (var pd in packetDescriptors)
                        {
                            buf.AsSpan(sourceOffset, (int)pd.actual_length).CopyTo(retBuf.AsSpan(destinationOffset));
                            sourceOffset += (int)pd.length;
                            destinationOffset += (int)pd.actual_length;
                        }
                    }

                    await Stream.WriteAsync(header.ToBytes(), cancellationToken);
                    if (basic.direction == UsbIpDir.USBIP_DIR_IN)
                    {
                        await Stream.WriteAsync(retBuf, cancellationToken);
                    }
                    await Stream.WriteAsync(packetDescriptors.ToBytes(), cancellationToken);
                }, cancellationToken, TaskScheduler.Default);
            }
            finally
            {
                _ = Task.WhenAll(ioctls).ContinueWith((task) =>
                {
                    gcHandle.Free();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        async Task HandleSubmitAsync(UsbIpHeaderBasic basic, UsbIpHeaderCmdSubmit submit, CancellationToken cancellationToken)
        {
            // We are synchronous with the receiver.

            var transferType = ConfigurationDescriptors.GetEndpointType(basic.ep, basic.direction == UsbIpDir.USBIP_DIR_IN);
            if (transferType == Constants.USB_ENDPOINT_TYPE_ISOCHRONOUS)
            {
                await HandleSubmitIsochronousAsync(basic, submit, cancellationToken);
                return;
            }

            var urb = new UsbSupUrb()
            {
                ep = basic.ep,
                dir = (basic.direction == UsbIpDir.USBIP_DIR_IN) ? UsbSupDirection.USBSUP_DIRECTION_IN : UsbSupDirection.USBSUP_DIRECTION_OUT,
                flags = (basic.direction == UsbIpDir.USBIP_DIR_IN)
                    ? (((submit.transfer_flags & 1) != 0) ? UsbSupXferFlags.USBSUP_FLAG_NONE : UsbSupXferFlags.USBSUP_FLAG_SHORT_OK)
                    : UsbSupXferFlags.USBSUP_FLAG_NONE,
                error = UsbSupError.USBSUP_XFER_OK,
                len = submit.transfer_buffer_length,
                numIsoPkts = (uint)submit.number_of_packets,
                aIsoPkts = new UsbSupIsoPkt[8],
            };

            var requestLength = submit.transfer_buffer_length;
            var payloadOffset = 0;
            switch (transferType)
            {
                case Constants.USB_ENDPOINT_TYPE_CONTROL:
                    urb.type = UsbSupTransferType.USBSUP_TRANSFER_TYPE_MSG;
                    payloadOffset = Marshal.SizeOf<USB_DEFAULT_PIPE_SETUP_PACKET>();
                    urb.len += (uint)payloadOffset;
                    break;
                case Constants.USB_ENDPOINT_TYPE_BULK:
                    urb.type = UsbSupTransferType.USBSUP_TRANSFER_TYPE_BULK;
                    break;
                case Constants.USB_ENDPOINT_TYPE_INTERRUPT:
                    urb.type = UsbSupTransferType.USBSUP_TRANSFER_TYPE_INTR;
                    break;
                default:
                    throw new UnexpectedResultException($"unknown endpoint type {transferType}");
            }

            var bytes = new byte[Marshal.SizeOf<UsbSupUrb>()];
            var buf = new byte[urb.len];

            if (transferType == Constants.USB_ENDPOINT_TYPE_CONTROL)
            {
                StructToBytes(submit.setup, buf);
            }

            if (basic.direction == UsbIpDir.USBIP_DIR_OUT)
            {
                await Stream.ReadExactlyAsync(buf.AsMemory()[payloadOffset..], cancellationToken);
            }

            // We now have received the entire SUBMIT request:
            // - If the request is "special" (reconfig, clear), then we will handle it immediately and await the result.
            //   This means no further requests will be read until the special request has completed.
            // - Otherwise, we will start a new task so that the receiver can continue.
            //   This means multiple URBs can be outstanding awaiting completion.
            //   The pending URBs can be completed out of order, but the replies must be sent atomically.

            Task ioctl;
            var pending = false;

            if ((basic.ep == 0)
                && (submit.setup.bmRequestType.B == Constants.BMREQUEST_TO_DEVICE)
                && (submit.setup.bRequest == Constants.USB_REQUEST_SET_CONFIGURATION))
            {
                // VBoxUsb needs this to get the endpoint handles
                var setConfig = new UsbSupSetConfig()
                {
                    bConfigurationValue = submit.setup.wValue.Anonymous.LowByte,
                };
                Logger.LogDebug($"Trapped SET_CONFIGURATION: {setConfig.bConfigurationValue}");
                await Device.IoControlAsync(SUPUSB_IOCTL.USB_SET_CONFIG, StructToBytes(setConfig), null);
                ioctl = Task.CompletedTask;
                ConfigurationDescriptors.SetConfiguration(setConfig.bConfigurationValue);
            }
            else if ((basic.ep == 0)
                && (submit.setup.bmRequestType.B == Constants.BMREQUEST_TO_INTERFACE)
                && (submit.setup.bRequest == Constants.USB_REQUEST_SET_INTERFACE))
            {
                // VBoxUsb needs this to get the endpoint handles
                var selectInterface = new UsbSupSelectInterface()
                {
                    bInterfaceNumber = submit.setup.wIndex.Anonymous.LowByte,
                    bAlternateSetting = submit.setup.wValue.Anonymous.LowByte,
                };
                Logger.LogDebug($"Trapped SET_INTERFACE: {selectInterface.bInterfaceNumber} -> {selectInterface.bAlternateSetting}");
                await Device.IoControlAsync(SUPUSB_IOCTL.USB_SELECT_INTERFACE, StructToBytes(selectInterface), null);
                ioctl = Task.CompletedTask;
                ConfigurationDescriptors.SetInterface(selectInterface.bInterfaceNumber, selectInterface.bAlternateSetting);
            }
            else if ((basic.ep == 0)
                && (submit.setup.bmRequestType.B == Constants.BMREQUEST_TO_ENDPOINT)
                && (submit.setup.bRequest == Constants.USB_REQUEST_CLEAR_FEATURE)
                && (submit.setup.wValue.W == 0))
            {
                // VBoxUsb needs this to notify the host controller
                var clearEndpoint = new UsbSupClearEndpoint()
                {
                    bEndpoint = submit.setup.wIndex.Anonymous.LowByte,
                };
                Logger.LogDebug($"Trapped CLEAR_FEATURE: {clearEndpoint.bEndpoint}");
                await Device.IoControlAsync(SUPUSB_IOCTL.USB_CLEAR_ENDPOINT, StructToBytes(clearEndpoint), null);
                ioctl = Task.CompletedTask;
            }
            else
            {
                if (transferType == Constants.USB_ENDPOINT_TYPE_CONTROL)
                {
                    Logger.LogTrace($"{submit.setup.bmRequestType.B} {submit.setup.bRequest} {submit.setup.wValue.W} {submit.setup.wIndex.W} {submit.setup.wLength}");
                }
                lock (PendingSubmitsLock)
                {
                    // To support UNLINK, we must be able to abort the pipe that is used for this URB.
                    // We need the raw USB endpoint number, i.e. including the high bit for input pipes.
                    if (!PendingSubmits.TryAdd(basic.seqnum, (byte)(basic.ep | (basic.direction == UsbIpDir.USBIP_DIR_IN ? 0x80u : 0x00u))))
                    {
                        throw new ProtocolViolationException($"duplicate sequence number {basic.seqnum}");
                    }
                    Logger.LogTrace($"Scheduled seqnum={basic.seqnum}, pending count = {PendingSubmits.Count}");
                }
                pending = true;
                // Input or output, exceptions or not, this buffer must be locked until after the ioctl has completed.
                var gcHandle = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    urb.buf = gcHandle.AddrOfPinnedObject();
                    StructToBytes(urb, bytes);
                    ioctl = Device.IoControlAsync(SUPUSB_IOCTL.SEND_URB, bytes, bytes);
                }
                catch
                {
                    gcHandle.Free();
                    throw;
                }
                _ = ioctl.ContinueWith((task) =>
                {
                    gcHandle.Free();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            // At this point we have initiated the ioctl (and possibly awaited it for special cases).
            // Now we schedule a continuation to write the reponse once the ioctl completes.
            // This is fire-and-forget; we'll return to the caller so it can already receive the next request.

            _ = ioctl.ContinueWith(async (task, state) =>
            {
                using var writeLock = await Lock.CreateAsync(WriteMutex, cancellationToken);

                // Now we are synchronous with the sender.

                if (pending)
                {
                    lock (PendingSubmitsLock)
                    {
                        // We are racing with possible UNLINK commands.
                        if (!PendingSubmits.Remove(basic.seqnum))
                        {
                            // Apparently, the client has already UNLINK-ed (canceled) the request; we're done.
                            Logger.LogTrace($"Completed seqnum={basic.seqnum} after UNLINK, pending count = {PendingSubmits.Count}");
                            return;
                        }
                    }
                    BytesToStruct(bytes, out urb);
                }

                var header = new UsbIpHeader
                {
                    basic = new UsbIpHeaderBasic
                    {
                        command = UsbIpCmd.USBIP_RET_SUBMIT,
                        seqnum = basic.seqnum,
                    },
                    ret_submit = new UsbIpHeaderRetSubmit()
                    {
                        status = -(int)Errno.SUCCESS,
                        actual_length = (int)urb.len,
                        start_frame = submit.start_frame,
                        number_of_packets = (int)urb.numIsoPkts,
                        error_count = 0,
                    },
                };

                if (transferType == Constants.USB_ENDPOINT_TYPE_CONTROL)
                {
                    header.ret_submit.actual_length = (header.ret_submit.actual_length > payloadOffset) ? (header.ret_submit.actual_length - payloadOffset) : 0;
                }

                if (urb.error != UsbSupError.USBSUP_XFER_OK)
                {
                    Logger.LogDebug($"{urb.error} -> {ConvertError(urb.error)} -> {header.ret_submit.status}");
                }
                Logger.LogTrace($"actual: {header.ret_submit.actual_length}, requested: {requestLength}");

                await Stream.WriteAsync(header.ToBytes(), cancellationToken);
                if (basic.direction == UsbIpDir.USBIP_DIR_IN)
                {
                    await Stream.WriteAsync(buf.AsMemory(payloadOffset, header.ret_submit.actual_length), cancellationToken);
                }
            }, cancellationToken, TaskScheduler.Default);
        }

        async Task HandleUnlinkAsync(UsbIpHeaderBasic basic, UsbIpHeaderCmdUnlink unlink, CancellationToken cancellationToken)
        {
            // We are synchronous with the receiver.

            var pending = false;
            byte endpoint;

            lock (PendingSubmitsLock)
            {
                pending = PendingSubmits.Remove(unlink.seqnum, out endpoint);
                Logger.LogTrace($"Unlinking {unlink.seqnum}, pending = {pending}, pending count = {PendingSubmits.Count}");
            }

            if (pending)
            {
                // VBoxUSB does not support canceling ioctls, so we will abort the pipe, which effectively cancels all URBs to that endpoint.
                // This is OK, since Linux will normally unlink all URBs anyway in quick succession.
                var clearEndpoint = new UsbSupClearEndpoint()
                {
                    bEndpoint = endpoint,
                };
                // Just like for CLEAR_FEATURE, we are going to wait until this finishes,
                // in order to avoid races with subsequent SUBMIT to the same endpoint.
                Logger.LogTrace($"Aborting endpoint {endpoint}");
                await Device.IoControlAsync(SUPUSB_IOCTL.USB_ABORT_ENDPOINT, StructToBytes(clearEndpoint), null);
            }

            using var writeLock = await Lock.CreateAsync(WriteMutex, cancellationToken);

            // Now we are synchronous with the sender.

            var header = new UsbIpHeader
            {
                basic = new UsbIpHeaderBasic
                {
                    command = UsbIpCmd.USBIP_RET_UNLINK,
                    seqnum = basic.seqnum,
                },
                ret_submit = new UsbIpHeaderRetSubmit()
                {
                    status = -(int)(pending ? Errno.ECONNRESET : Errno.SUCCESS),
                },
            };

            await Stream.WriteAsync(header.ToBytes(), cancellationToken);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var header = await Stream.ReadUsbIpHeaderAsync(cancellationToken);
                switch (header.basic.command)
                {
                    case UsbIpCmd.USBIP_CMD_SUBMIT:
                        Logger.LogTrace($"USBIP_CMD_SUBMIT, seqnum={header.basic.seqnum}, flags={header.cmd_submit.transfer_flags}, " +
                                $"length={header.cmd_submit.transfer_buffer_length}, ep={header.basic.ep}");
                        await HandleSubmitAsync(header.basic, header.cmd_submit, cancellationToken);
                        break;
                    case UsbIpCmd.USBIP_CMD_UNLINK:
                        Logger.LogTrace($"USBIP_CMD_UNLINK, seqnum={header.basic.seqnum}, unlink_seqnum={header.cmd_unlink.seqnum}");
                        await HandleUnlinkAsync(header.basic, header.cmd_unlink, cancellationToken);
                        break;
                    default:
                        throw new ProtocolViolationException($"unknown UsbIpCmd {header.basic.command}");
                }
            }
        }
    }
}

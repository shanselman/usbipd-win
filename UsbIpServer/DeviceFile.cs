﻿// SPDX-FileCopyrightText: 2020 Frans van Dorsselaer
//
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.SystemServices;

namespace UsbIpServer
{
    sealed class DeviceFile : IDisposable
    {
        public DeviceFile(string fileName)
        {
            handle = PInvoke.CreateFile(fileName, FILE_ACCESS_FLAGS.FILE_READ_DATA | FILE_ACCESS_FLAGS.FILE_WRITE_DATA,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED, null);

            if (handle.IsInvalid)
            {
                throw new Win32Exception("CreateFile");
            }
            if (!ThreadPool.BindHandle(handle))
            {
                handle.Dispose();
                throw new UnexpectedResultException("ThreadPool.BindHandle() failed");
            }
        }

        readonly SafeFileHandle handle;

        Task<uint> IoControlAsync(uint ioControlCode, byte[]? input, byte[]? output, bool exactOutput = true)
        {
            var taskCompletionSource = new TaskCompletionSource<uint>();
            var overlapped = new Overlapped();

            unsafe
            {
                void OnCompletion(uint errorCode, uint numBytes, NativeOverlapped* nativeOverlapped)
                {
                    if ((WIN32_ERROR)errorCode == WIN32_ERROR.ERROR_SUCCESS)
                    {
                        if (exactOutput && ((output?.Length ?? 0) != numBytes))
                        {
                            taskCompletionSource.SetException(new ProtocolViolationException($"DeviceIoControl returned {numBytes} bytes, expected {output?.Length ?? 0}"));
                        }
                        else
                        {
                            taskCompletionSource.SetResult(numBytes);
                        }
                    }
                    else
                    {
                        taskCompletionSource.SetException(new Win32Exception((int)errorCode, $"DeviceIoControl returned error {(WIN32_ERROR)errorCode}"));
                    }
                    Overlapped.Free(nativeOverlapped);
                }

                var nativeOverlapped = overlapped.Pack(OnCompletion, new object?[] { input, output });
                fixed (byte* pInput = input, pOutput = output)
                {
                    if (!PInvoke.DeviceIoControl(handle, ioControlCode, pInput, (uint)(input?.Length ?? 0),
                        pOutput, (uint)(output?.Length ?? 0), null, (OVERLAPPED *)nativeOverlapped))
                    {
                        var errorCode = (WIN32_ERROR)Marshal.GetLastWin32Error();
                        if (errorCode != WIN32_ERROR.ERROR_IO_PENDING)
                        {
                            OnCompletion((uint)errorCode, 0, nativeOverlapped);
                        }
                    }
                }
            }
            return taskCompletionSource.Task;
        }

        public Task<uint> IoControlAsync<T>(T ioControlCode, byte[]? input, byte[]? output, bool exactOutput = true) where T : Enum
        {
            return IoControlAsync((uint)(object)ioControlCode, input, output, exactOutput);
        }

        bool IsDisposed;
        public void Dispose()
        {
            if (!IsDisposed)
            {
                handle.Dispose();
                IsDisposed = true;
            }
        }
    }
}

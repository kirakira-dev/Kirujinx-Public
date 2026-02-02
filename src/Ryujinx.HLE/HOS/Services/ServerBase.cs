using Microsoft.IO;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Ipc;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Horizon;
using Ryujinx.Horizon.Common;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services
{
    class ServerBase : IDisposable
    {
        // Must be the maximum value used by services (highest one know is the one used by nvservices = 0x8000).
        // Having a size that is too low will cause failures as data copy will fail if the receiving buffer is
        // not large enough.
        private const int PointerBufferSize = 0x8000;

        private static uint[] DefaultCapabilities => [
            (((uint)KScheduler.CpuCoresCount - 1) << 24) + (((uint)KScheduler.CpuCoresCount - 1) << 16) + 0x63F7u,
            0x1FFFFFCF,
            0x207FFFEF,
            0x47E0060F,
            0x0048BFFF,
            0x01007FFF,
        ];

        // The amount of time Dispose() will wait to Join() the thread executing the ServerLoop()
        private static readonly TimeSpan _threadJoinTimeout = TimeSpan.FromSeconds(3);

        private readonly KernelContext _context;
        private KProcess _selfProcess;
        private readonly int _workerCount;

        private KEvent _wakeEvent;
        private int _wakeHandle = 0;
        private ulong _heapBaseAddress;

        private readonly ReaderWriterLockSlim _handleLock = new();
        private readonly Dictionary<int, IpcService> _sessions = new();
        private readonly Dictionary<int, Func<IpcService>> _ports = new();

        private readonly object _workerStatesLock = new();
        private readonly List<WorkerThreadState> _workerStates = [];
        private readonly List<KThread> _allThreads = [];

        private int _isDisposed = 0;

        public ManualResetEvent InitDone { get; }
        public string Name { get; }
        public Func<IpcService> SmObjectFactory { get; }

        public ServerBase(KernelContext context, string name, Func<IpcService> smObjectFactory = null, int workerCount = 1)
        {
            _context = context;
            _workerCount = Math.Max(1, workerCount);

            InitDone = new ManualResetEvent(false);
            Name = name;
            SmObjectFactory = smObjectFactory;

            const ProcessCreationFlags Flags =
                ProcessCreationFlags.EnableAslr |
                ProcessCreationFlags.AddressSpace64Bit |
                ProcessCreationFlags.Is64Bit |
                ProcessCreationFlags.PoolPartitionSystem;

            ProcessCreationInfo creationInfo = new("Service", 1, 0, 0x8000000, 1, Flags, 0, 0);

            KernelStatic.StartInitialProcess(context, creationInfo, DefaultCapabilities, 44, Main);
        }
        private sealed class WorkerThreadState : IDisposable
        {
            public KThread Thread { get; }
            public int Index { get; }
            public RecyclableMemoryStream RequestStream { get; }
            public BinaryReader RequestReader { get; }
            public RecyclableMemoryStream ResponseStream { get; }
            public BinaryWriter ResponseWriter { get; }

            public WorkerThreadState(KThread thread, int index)
            {
                Thread = thread ?? throw new ArgumentNullException(nameof(thread));
                Index = index;

                RequestStream = MemoryStreamManager.Shared.GetStream();
                RequestReader = new BinaryReader(RequestStream, System.Text.Encoding.UTF8, leaveOpen: true);

                ResponseStream = MemoryStreamManager.Shared.GetStream();
                ResponseWriter = new BinaryWriter(ResponseStream, System.Text.Encoding.UTF8, leaveOpen: true);
            }

            public ulong MessagePtr => Thread.TlsAddress;

            public void Dispose()
            {
                ResponseWriter.Dispose();
                ResponseStream.Dispose();
                RequestReader.Dispose();
                RequestStream.Dispose();
            }
        }

        private void AddPort(int serverPortHandle, Func<IpcService> objectFactory)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);

                _ports.Add(serverPortHandle, objectFactory);
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitWriteLock();
                }
            }
        }

        public void AddSessionObj(KServerSession serverSession, IpcService obj)
        {
            // Ensure that the sever loop is running.
            InitDone.WaitOne();

            _selfProcess.HandleTable.GenerateHandle(serverSession, out int serverSessionHandle);

            AddSessionObj(serverSessionHandle, obj);
        }

        public void AddSessionObj(int serverSessionHandle, IpcService obj)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);

                _sessions.Add(serverSessionHandle, obj);
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitWriteLock();
                }
            }

            _wakeEvent.WritableEvent.Signal();
        }

        private IpcService GetSessionObj(int serverSessionHandle)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterReadLock(Timeout.Infinite);

                return _sessions[serverSessionHandle];
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitReadLock();
                }
            }
        }

        private bool RemoveSessionObj(int serverSessionHandle, out IpcService obj)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);

                return _sessions.Remove(serverSessionHandle, out obj);
            }
            finally
            {
                if (lockTaken)
                {
                    _handleLock.ExitWriteLock();
                }
            }
        }

        private void Main()
        {
            ServerLoop();
        }

        private WorkerThreadState RegisterWorkerState(KThread thread, int index)
        {
            thread.HostThread.Name = $"HLE.OsThread.{thread.ThreadUid} ({Name}#{index})";

            WorkerThreadState state = new(thread, index);

            lock (_workerStatesLock)
            {
                _workerStates.Add(state);

                if (!_allThreads.Contains(thread))
                {
                    _allThreads.Add(thread);
                }
            }

            return state;
        }

        private void UnregisterWorkerState(WorkerThreadState state)
        {
            lock (_workerStatesLock)
            {
                _workerStates.Remove(state);
            }

            state.Dispose();
        }

        private void ServerLoop()
        {
            _selfProcess = KernelStatic.GetCurrentProcess();
            KThread currentThread = KernelStatic.GetCurrentThread();

            WorkerThreadState mainState = RegisterWorkerState(currentThread, 0);

            HorizonStatic.Register(
                default,
                _context.Syscall,
                _selfProcess.CpuMemory,
                currentThread.ThreadContext,
                (int)currentThread.ThreadContext.GetX(1));

            if (SmObjectFactory != null)
            {
                _context.Syscall.ManageNamedPort(out int serverPortHandle, "sm:", 50);

                AddPort(serverPortHandle, SmObjectFactory);
            }

            _wakeEvent = new KEvent(_context);
            Result result = _selfProcess.HandleTable.GenerateHandle(_wakeEvent.ReadableEvent, out _wakeHandle);
            result.AbortOnFailure();

            ulong messagePtr = mainState.MessagePtr;
            _context.Syscall.SetHeapSize(out ulong heapAddr, 0x200000);
            _heapBaseAddress = heapAddr;

            _selfProcess.CpuMemory.Write(messagePtr + 0x0, 0);
            _selfProcess.CpuMemory.Write(messagePtr + 0x4, 2 << 10);
            _selfProcess.CpuMemory.Write(messagePtr + 0x8, heapAddr | ((ulong)PointerBufferSize << 48));

            StartAdditionalWorkerThreads(currentThread);

            InitDone.Set();

            RunWorkerLoop(mainState);

            UnregisterWorkerState(mainState);

            Dispose();
        }

        private void StartAdditionalWorkerThreads(KThread templateThread)
        {
            if (_workerCount <= 1)
            {
                return;
            }

            int priority = templateThread.DynamicPriority;
            int core = templateThread.PreferredCore;

            for (int index = 1; index < _workerCount; index++)
            {
                int capturedIndex = index;
                int handle = 0;
                Result createResult = _context.Syscall.CreateThread(
                    out handle,
                    0,
                    0,
                    0,
                    priority,
                    core,
                    () => WorkerThreadEntry(capturedIndex, handle));

                createResult.AbortOnFailure();

                _context.Syscall.StartThread(handle).AbortOnFailure();
                _context.Syscall.CloseHandle(handle);
            }
        }

        private void WorkerThreadEntry(int index, int threadHandle)
        {
            // Ensure the worker sees the shared process reference.
            _selfProcess = KernelStatic.GetCurrentProcess();

            KThread thread = KernelStatic.GetCurrentThread();
            WorkerThreadState state = RegisterWorkerState(thread, index);

            HorizonStatic.Register(
                default,
                _context.Syscall,
                _selfProcess.CpuMemory,
                thread.ThreadContext,
                threadHandle);

            _selfProcess.CpuMemory.Write(state.MessagePtr + 0x0, 0);
            _selfProcess.CpuMemory.Write(state.MessagePtr + 0x4, 2 << 10);
            _selfProcess.CpuMemory.Write(state.MessagePtr + 0x8, _heapBaseAddress | ((ulong)PointerBufferSize << 48));

            RunWorkerLoop(state);

            UnregisterWorkerState(state);
        }

        private void RunWorkerLoop(WorkerThreadState state)
        {
            int replyTargetHandle = 0;

            while (true)
            {
                int portHandleCount;
                int handleCount;
                int[] handles;

                bool handleLockTaken = false;
                try
                {
                    handleLockTaken = _handleLock.TryEnterReadLock(Timeout.Infinite);

                    portHandleCount = _ports.Count;

                    handleCount = portHandleCount + _sessions.Count + 1;

                    handles = ArrayPool<int>.Shared.Rent(handleCount);

                    handles[0] = _wakeHandle;

                    _ports.Keys.CopyTo(handles, 1);

                    _sessions.Keys.CopyTo(handles, portHandleCount + 1);
                }
                finally
                {
                    if (handleLockTaken)
                    {
                        _handleLock.ExitReadLock();
                    }
                }

                Result rc = _context.Syscall.ReplyAndReceive(out int signaledIndex, handles.AsSpan(0, handleCount), replyTargetHandle, -1);

                state.Thread.HandlePostSyscall();

                if (!state.Thread.Context.Running)
                {
                    ArrayPool<int>.Shared.Return(handles);
                    break;
                }

                replyTargetHandle = 0;

                if (rc == Result.Success && signaledIndex >= portHandleCount + 1)
                {
                    // We got a IPC request, process it, pass to the appropriate service if needed.
                    int signaledHandle = handles[signaledIndex];

                    if (Process(state, signaledHandle))
                    {
                        replyTargetHandle = signaledHandle;
                    }
                }
                else
                {
                    if (rc == Result.Success)
                    {
                        if (signaledIndex > 0)
                        {
                            // We got a new connection, accept the session to allow servicing future requests.
                            if (_context.Syscall.AcceptSession(out int serverSessionHandle, handles[signaledIndex]) == Result.Success)
                            {
                                bool handleWriteLockTaken = false;
                                try
                                {
                                    handleWriteLockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);
                                    IpcService obj = _ports[handles[signaledIndex]].Invoke();
                                    _sessions.Add(serverSessionHandle, obj);
                                }
                                finally
                                {
                                    if (handleWriteLockTaken)
                                    {
                                        _handleLock.ExitWriteLock();
                                    }
                                }
                            }
                        }
                        else
                        {
                            // The _wakeEvent signalled, which means we have a new session.
                            _wakeEvent.WritableEvent.Clear();
                        }
                    }
                    else if (rc == KernelResult.PortRemoteClosed && signaledIndex >= 0 && SmObjectFactory != null)
                    {
                        DestroySession(handles[signaledIndex]);
                    }

                    _selfProcess.CpuMemory.Write(state.MessagePtr + 0x0, 0);
                    _selfProcess.CpuMemory.Write(state.MessagePtr + 0x4, 2 << 10);
                    _selfProcess.CpuMemory.Write(state.MessagePtr + 0x8, _heapBaseAddress | ((ulong)PointerBufferSize << 48));
                }

                ArrayPool<int>.Shared.Return(handles);
            }
        }

        private void DestroySession(int serverSessionHandle)
        {
            _context.Syscall.CloseHandle(serverSessionHandle);

            if (RemoveSessionObj(serverSessionHandle, out IpcService session))
            {
                (session as IDisposable)?.Dispose();
            }
        }

        private bool Process(WorkerThreadState state, int serverSessionHandle)
        {
            IpcMessage request = ReadRequest(state);

            if (request.Type == IpcMessageType.CmifResponse)
            {
                // No new request was delivered; nothing to reply to.
                return false;
            }

            IpcMessage response = new();
            KThread callerThread = null;

            KServerSession serverSession = _selfProcess.HandleTable.GetObject<KServerSession>(serverSessionHandle);

            if (serverSession != null)
            {
                callerThread = serverSession.GetActiveRequestClientThread();
            }

            ulong tempAddr = _heapBaseAddress;
            int sizesOffset = request.RawData.Length - ((request.RecvListBuff.Count * 2 + 3) & ~3);

            bool noReceive = true;

            for (int i = 0; i < request.ReceiveBuff.Count; i++)
            {
                noReceive &= (request.ReceiveBuff[i].Position == 0);
            }

            if (noReceive)
            {
                response.PtrBuff.EnsureCapacity(request.RecvListBuff.Count);

                for (int i = 0; i < request.RecvListBuff.Count; i++)
                {
                    ulong size = (ulong)BinaryPrimitives.ReadInt16LittleEndian(request.RawData.AsSpan(sizesOffset + i * 2, 2));

                    response.PtrBuff.Add(new IpcPtrBuffDesc(tempAddr, (uint)i, size));

                    request.RecvListBuff[i] = new IpcRecvListBuffDesc(tempAddr, size);

                    tempAddr += size;
                }
            }

            bool shouldReply = true;
            bool isTipcCommunication = false;

            state.RequestStream.SetLength(0);
            state.RequestStream.Write(request.RawData);
            state.RequestStream.Position = 0;

            if (request.Type is IpcMessageType.CmifRequest or
                IpcMessageType.CmifRequestWithContext)
            {
                response.Type = IpcMessageType.CmifResponse;

                state.ResponseStream.SetLength(0);

                ServiceCtx context = new(
                    _context.Device,
                    _selfProcess,
                    _selfProcess.CpuMemory,
                    state.Thread,
                    callerThread,
                    request,
                    response,
                    state.RequestReader,
                    state.ResponseWriter);

                GetSessionObj(serverSessionHandle).CallCmifMethod(context);

                response.RawData = state.ResponseStream.ToArray();
            }
            else if (request.Type is IpcMessageType.CmifControl or
                     IpcMessageType.CmifControlWithContext)
            {
#pragma warning disable IDE0059 // Remove unnecessary value assignment
                uint magic = (uint)state.RequestReader.ReadUInt64();
#pragma warning restore IDE0059
                uint cmdId = (uint)state.RequestReader.ReadUInt64();

                switch (cmdId)
                {
                    case 0:
                        FillHipcResponse(state, response, 0, GetSessionObj(serverSessionHandle).ConvertToDomain());
                        break;

                    case 3:
                        FillHipcResponse(state, response, 0, PointerBufferSize);
                        break;

                    // TODO: Whats the difference between IpcDuplicateSession/Ex?
                    case 2:
                    case 4:
                        {
                            _ = state.RequestReader.ReadInt32();

                            _context.Syscall.CreateSession(out int dupServerSessionHandle, out int dupClientSessionHandle, false, 0);

                            bool writeLockTaken = false;
                            try
                            {
                                writeLockTaken = _handleLock.TryEnterWriteLock(Timeout.Infinite);
                                _sessions[dupServerSessionHandle] = _sessions[serverSessionHandle];
                            }
                            finally
                            {
                                if (writeLockTaken)
                                {
                                    _handleLock.ExitWriteLock();
                                }
                            }

                            response.HandleDesc = IpcHandleDesc.MakeMove(dupClientSessionHandle);

                            FillHipcResponse(state, response, 0);

                            break;
                        }

                    default:
                        throw new NotImplementedException(cmdId.ToString());
                }
            }
            else if (request.Type is IpcMessageType.CmifCloseSession or IpcMessageType.TipcCloseSession)
            {
                DestroySession(serverSessionHandle);
                shouldReply = false;
            }
            // If the type is past 0xF, we are using TIPC
            else if (request.Type > IpcMessageType.TipcCloseSession)
            {
                isTipcCommunication = true;

                // Response type is always the same as request on TIPC.
                response.Type = request.Type;

                state.ResponseStream.SetLength(0);

                ServiceCtx context = new(
                    _context.Device,
                    _selfProcess,
                    _selfProcess.CpuMemory,
                    state.Thread,
                    callerThread,
                    request,
                    response,
                    state.RequestReader,
                    state.ResponseWriter);

                GetSessionObj(serverSessionHandle).CallTipcMethod(context);

                response.RawData = state.ResponseStream.ToArray();

                RecyclableMemoryStream responseStream = response.GetStreamTipc();
                _selfProcess.CpuMemory.Write(state.MessagePtr, responseStream.GetReadOnlySequence());
                MemoryStreamManager.Shared.ReleaseStream(responseStream);
            }
            else
            {
                throw new NotImplementedException(request.Type.ToString());
            }

            if (!isTipcCommunication)
            {
                RecyclableMemoryStream responseStream = response.GetStream((long)state.MessagePtr, _heapBaseAddress | ((ulong)PointerBufferSize << 48));
                _selfProcess.CpuMemory.Write(state.MessagePtr, responseStream.GetReadOnlySequence());
                MemoryStreamManager.Shared.ReleaseStream(responseStream);
            }

            return shouldReply;
        }

        private IpcMessage ReadRequest(WorkerThreadState state)
        {
            const int MessageSize = 0x100;

            using SpanOwner<byte> reqDataOwner = SpanOwner<byte>.Rent(MessageSize);

            Span<byte> reqDataSpan = reqDataOwner.Span;

            _selfProcess.CpuMemory.Read(state.MessagePtr, reqDataSpan);

            IpcMessage request = new(reqDataSpan, (long)state.MessagePtr);

            return request;
        }

        private void FillHipcResponse(WorkerThreadState state, IpcMessage response, long result)
        {
            FillHipcResponse(state, response, result, ReadOnlySpan<byte>.Empty);
        }

        private void FillHipcResponse(WorkerThreadState state, IpcMessage response, long result, int value)
        {
            Span<byte> span = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            FillHipcResponse(state, response, result, span);
        }

        private void FillHipcResponse(WorkerThreadState state, IpcMessage response, long result, ReadOnlySpan<byte> data)
        {
            response.Type = IpcMessageType.CmifResponse;

            state.ResponseStream.SetLength(0);

            state.ResponseStream.Write(IpcMagic.Sfco);
            state.ResponseStream.Write(result);

            state.ResponseStream.Write(data);

            response.RawData = state.ResponseStream.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            List<KThread> threadsToJoin;

            lock (_workerStatesLock)
            {
                threadsToJoin = new List<KThread>(_allThreads);
            }

            int currentManagedId = Environment.CurrentManagedThreadId;

            foreach (KThread thread in threadsToJoin)
            {
                Thread hostThread = thread.HostThread;

                if (hostThread == null || !hostThread.IsAlive)
                {
                    continue;
                }

                if (hostThread.ManagedThreadId == currentManagedId)
                {
                    continue;
                }

                if (!hostThread.Join(_threadJoinTimeout))
                {
                    Logger.Warning?.Print(LogClass.Service, $"The ServerBase worker thread didn't terminate within {_threadJoinTimeout:g}, waiting longer.");

                    hostThread.Join(Timeout.Infinite);
                }
            }

            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                if (_wakeHandle != 0 && _selfProcess != null)
                {
                    _selfProcess.HandleTable.CloseHandle(_wakeHandle);
                }

                foreach (IpcService service in _sessions.Values)
                {
                    (service as IDisposable)?.Dispose();
                    service.DestroyAtExit();
                }

                _sessions.Clear();
                _ports.Clear();
                _handleLock.Dispose();

                InitDone.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}

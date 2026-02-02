using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Utilities
{
    public static class SocketPriorityManager
    {
        private static bool _prioritizeSocketConnections;
        private static readonly ConcurrentDictionary<int, SocketStats> _socketStats;
        private static int _activeConnections;
        private static int _pendingOperations;
        private static long _totalBytesReceived;
        private static long _totalBytesSent;
        private static long _operationsCompleted;

        private const int HighPriorityThreshold = 3;

        static SocketPriorityManager()
        {
            _socketStats = new ConcurrentDictionary<int, SocketStats>();
        }

        public static bool PrioritizeSocketConnections
        {
            get => _prioritizeSocketConnections;
            set => _prioritizeSocketConnections = value;
        }

        public static int ActiveConnections => _activeConnections;
        public static int PendingOperations => _pendingOperations;
        public static long TotalBytesReceived => _totalBytesReceived;
        public static long TotalBytesSent => _totalBytesSent;

        public static bool ShouldPrioritizeOverShaders()
        {
            if (!_prioritizeSocketConnections)
            {
                return false;
            }

            return _activeConnections > 0 || _pendingOperations > 0;
        }

        public static bool HasActiveNetworkActivity()
        {
            return _activeConnections > 0 || _pendingOperations > 0;
        }

        public static int GetSocketPriority()
        {
            if (!_prioritizeSocketConnections)
            {
                return 1;
            }

            if (_pendingOperations >= HighPriorityThreshold)
            {
                return 3;
            }
            else if (_activeConnections > 0)
            {
                return 2;
            }

            return 1;
        }

        public static void RegisterSocket(int socketFd)
        {
            _socketStats.TryAdd(socketFd, new SocketStats());
            Interlocked.Increment(ref _activeConnections);
        }

        public static void UnregisterSocket(int socketFd)
        {
            if (_socketStats.TryRemove(socketFd, out _))
            {
                Interlocked.Decrement(ref _activeConnections);
            }
        }

        public static void BeginOperation(int socketFd)
        {
            Interlocked.Increment(ref _pendingOperations);
            if (_socketStats.TryGetValue(socketFd, out SocketStats stats))
            {
                stats.BeginOperation();
            }
        }

        public static void EndOperation(int socketFd, int bytesTransferred, bool isSend)
        {
            Interlocked.Decrement(ref _pendingOperations);
            Interlocked.Increment(ref _operationsCompleted);

            if (bytesTransferred > 0)
            {
                if (isSend)
                {
                    Interlocked.Add(ref _totalBytesSent, bytesTransferred);
                }
                else
                {
                    Interlocked.Add(ref _totalBytesReceived, bytesTransferred);
                }
            }

            if (_socketStats.TryGetValue(socketFd, out SocketStats stats))
            {
                stats.EndOperation(bytesTransferred, isSend);
            }
        }

        public static void RecordConnect(int socketFd)
        {
            if (_socketStats.TryGetValue(socketFd, out SocketStats stats))
            {
                stats.MarkConnected();
            }
        }

        public static bool IsSocketActive(int socketFd)
        {
            if (_socketStats.TryGetValue(socketFd, out SocketStats stats))
            {
                return stats.IsActive;
            }
            return false;
        }

        public static void Reset()
        {
            _socketStats.Clear();
            Interlocked.Exchange(ref _activeConnections, 0);
            Interlocked.Exchange(ref _pendingOperations, 0);
            Interlocked.Exchange(ref _totalBytesReceived, 0);
            Interlocked.Exchange(ref _totalBytesSent, 0);
            Interlocked.Exchange(ref _operationsCompleted, 0);
        }

        public static string GetStatistics()
        {
            return $"Active: {_activeConnections}, Pending: {_pendingOperations}, " +
                   $"Recv: {_totalBytesReceived / 1024}KB, Sent: {_totalBytesSent / 1024}KB, " +
                   $"Ops: {_operationsCompleted}";
        }

        private class SocketStats
        {
            private int _pendingOps;
            private long _bytesReceived;
            private long _bytesSent;
            private long _lastActivityTicks;
            private bool _isConnected;

            private static readonly long ActivityTimeoutTicks = 5 * Stopwatch.Frequency;

            public bool IsActive => _isConnected && 
                (Stopwatch.GetTimestamp() - _lastActivityTicks) < ActivityTimeoutTicks;

            public void BeginOperation()
            {
                Interlocked.Increment(ref _pendingOps);
                _lastActivityTicks = Stopwatch.GetTimestamp();
            }

            public void EndOperation(int bytes, bool isSend)
            {
                Interlocked.Decrement(ref _pendingOps);
                _lastActivityTicks = Stopwatch.GetTimestamp();

                if (bytes > 0)
                {
                    if (isSend)
                    {
                        Interlocked.Add(ref _bytesSent, bytes);
                    }
                    else
                    {
                        Interlocked.Add(ref _bytesReceived, bytes);
                    }
                }
            }

            public void MarkConnected()
            {
                _isConnected = true;
                _lastActivityTicks = Stopwatch.GetTimestamp();
            }
        }
    }
}

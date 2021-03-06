﻿using Spreads.Buffers;
using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Spreads.SignalW.Client
{
    public abstract class Channel
    {
        public abstract ValueTask WriteAsync(MemoryStream item, bool disposeItem);

        public abstract ValueTask<bool> TryComplete();

        public abstract ValueTask<MemoryStream> ReadAsync();

        public abstract Task<Exception> Completion { get; }
    }

    public class WsChannel : Channel
    {
        private readonly WebSocket _ws;
        private readonly Format _format;

        private TaskCompletionSource<Exception> _tcs;
        private CancellationTokenSource _cts;

        //private readonly SemaphoreSlim _readSemaphore = new SemaphoreSlim(1, 1);
        //private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

        public WsChannel(WebSocket ws, Format format)
        {
            _ws = ws;
            _format = format;
            _tcs = new TaskCompletionSource<Exception>();
            _cts = new CancellationTokenSource();
        }

        public override ValueTask WriteAsync(MemoryStream item, bool disposeItem)
        {
            if (!(item is RecyclableMemoryStream rms)) // no supposed case
            {
                rms = RecyclableMemoryStreamManager.Default.GetStream(null, checked((int)item.Length));
                item.CopyTo(rms);
                if (disposeItem)
                {
                    item.Dispose();
                }
            }

            try
            {
                // TODO for now assume struct enums are free and do not need dispose, maybe refacor RMS later
                using (var e = rms.Chunks.GetEnumerator())
                {
                    var type = _format == Format.Binary
                        ? WebSocketMessageType.Binary
                        : WebSocketMessageType.Text;
                    ArraySegment<byte> chunk;
                    if (!e.MoveNext())
                    {
                        throw new ArgumentException("Item is empty");
                    }

                    chunk = e.Current;

                    var endOfMessage = !e.MoveNext();

                    if (!endOfMessage) // mutipart async
                    {
                        return WriteMultipartAsync(rms);
                    }

#if NETCOREAPP2_1
                    return _ws.SendAsync((ReadOnlyMemory<byte>)chunk, type, true, _cts.Token);
#else
                    return new ValueTask(_ws.SendAsync(chunk, type, true, _cts.Token));
#endif
                }
            }
            catch (Exception ex)
            {
                return CloseAsync(ex);
            }
            finally
            {
                if (disposeItem)
                {
                    rms.Dispose();
                }
            }
        }

        private async ValueTask WriteMultipartAsync(RecyclableMemoryStream rms)
        {
            using (var e = rms.Chunks.GetEnumerator())
            {
                var type = _format == Format.Binary
                    ? WebSocketMessageType.Binary
                    : WebSocketMessageType.Text;
                ArraySegment<byte> chunk;
                if (e.MoveNext())
                {
                    while (true)
                    {
                        chunk = e.Current;
                        var endOfMessage = !e.MoveNext();

#if NETCOREAPP2_1
                        await _ws.SendAsync((ReadOnlyMemory<byte>)chunk, type, endOfMessage, _cts.Token);

#else
                        await _ws.SendAsync(chunk, type, endOfMessage, _cts.Token);
#endif
                        if (endOfMessage) { break; }
                    }
                }
            }
        }

        private async ValueTask CloseAsync(Exception ex)
        {
            // Write not finished, Completion indicates why (null - cancelled)

            if (_cts.IsCancellationRequested)
            {
                _tcs.TrySetResult(null);
            }
            else
            {
                _cts.Cancel();
                _tcs.TrySetResult(ex);
            }
            // https://tools.ietf.org/html/rfc6455#section-5.5.1
            // always needed, even when we received Close
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure",
                CancellationToken.None);
        }

        public override ValueTask<bool> TryComplete()
        {
            if (_cts.IsCancellationRequested)
            {
                _tcs.TrySetResult(null);
                return new ValueTask<bool>(false);
            }
            _cts.Cancel();
            return new ValueTask<bool>(true);
        }

        public override ValueTask<MemoryStream> ReadAsync()
        {
            RecyclableMemoryStream rms = null;
            try
            {
#if NETCOREAPP2_1

                // Do a 0 byte read so that idle connections don't allocate a buffer when waiting for a read
                var t = _ws.ReceiveAsync(Memory<byte>.Empty, CancellationToken.None);

                if (!t.IsCompletedSuccessfully)
                {
                    return ContinueReadAsync(null);
                }

                // this will create the first chunk with default size

                rms = RecyclableMemoryStreamManager.Default.GetStream();

                var result = t.Result;
                if (result.MessageType != WebSocketMessageType.Close)
                {
                    // ReSharper disable once GenericEnumeratorNotDisposed
                    var r = rms.Chunks.GetEnumerator();
                    if (!r.MoveNext())
                    {
                        throw new System.ApplicationException("Fresh RMS has no chunks");
                    }
                    var firstChunk = r.Current;

                    // we have data, must be able to read
                    result = _ws.ReceiveAsync((Memory<byte>)firstChunk, _cts.Token).GetAwaiter().GetResult();
                    rms.SetLength(result.Count);

                    if (result.EndOfMessage)
                    {
                        return new ValueTask<MemoryStream>(rms);
                    }

                    return ContinueReadAsync(rms);
                }
#else
                return ContinueReadAsync(null);
#endif

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _cts.Cancel();
                }

#pragma warning disable 4014
                CloseAsync(null);
#pragma warning restore 4014
            }
            catch (Exception ex)
            {
#pragma warning disable 4014
                CloseAsync(ex);
#pragma warning restore 4014
            }
            rms?.Dispose();
            return new ValueTask<MemoryStream>((RecyclableMemoryStream)null);
        }

        private async ValueTask<MemoryStream> ContinueReadAsync(RecyclableMemoryStream rms)
        {
            // we have rms with length set to zero of first sync result
            rms = rms ?? RecyclableMemoryStreamManager.Default.GetStream();

            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
#if NETCOREAPP2_1
                // Do a 0 byte read so that idle connections don't allocate a buffer when waiting for a read
                var result = await _ws.ReceiveAsync(Memory<byte>.Empty, CancellationToken.None);
                if (result.MessageType != WebSocketMessageType.Close)
                {
                    result = await _ws.ReceiveAsync((Memory<byte>)buffer, _cts.Token);
                }
#else
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
#endif
                while (result.MessageType != WebSocketMessageType.Close && !_cts.IsCancellationRequested)
                {
                    rms.Write(buffer, 0, result.Count);

                    if (!result.EndOfMessage)
                    {
#if NETCOREAPP2_1
                        result = await _ws.ReceiveAsync((Memory<byte>)buffer, _cts.Token);
#else
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
#endif
                    }
                    else
                    {
                        return rms;
                    }
                }

                // closing now
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _cts.Cancel();
                }

                await CloseAsync(null);
            }
            catch (Exception ex)
            {
                await CloseAsync(ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            rms.Dispose();
            return null;
        }

        public override Task<Exception> Completion => _tcs.Task;
    }
}

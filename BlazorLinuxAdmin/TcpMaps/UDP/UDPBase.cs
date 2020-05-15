using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BlazorLinuxAdmin.TcpMaps.UDP
{
	public enum UDPPackageType : long
	{
		Unknown = 0
			,
		SessionConnect, SessionPrepair, SessionConfirm, SessionReady, SessionClose, SessionIdle, SessionError, DataPost, DataRead, DataMiss, DataPing
	}
	public class UDPConnectJson
	{
		public string token { get; set; }
		public string code { get; set; }

		static public UDPConnectJson Deserialize(string expr)
		{
			try
			{
				return System.Text.Json.JsonSerializer.Deserialize<UDPConnectJson>(expr);
			}
			catch (Exception)
			{
				//Console.WriteLine("Error: UDPConnectJson Deserialize " + expr);
				throw;
			}
		}
	}
	public static class UDPMeta
	{
		public const int BestUDPSize = 1350;

		const byte pt0 = 31;
		const byte pt1 = 41;
		const byte pt2 = 59;
		const byte pt3 = 26;
		const byte pt4 = 53;
		const byte pt5 = 58;
		const byte pt6 = 97;

		static public byte[] CreateSessionConnect(long sessionid, string token)
		{

			byte[] jsonbuff = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new UDPConnectJson() { token = token }));
			byte[] allbuff = new byte[16 + jsonbuff.Length];
			SetPackageType(allbuff, UDPPackageType.SessionConnect);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(jsonbuff, 0, allbuff, 16, jsonbuff.Length);
			return allbuff;
		}

		static public byte[] CreateSessionPrepair(long sessionid, string token)
		{
			byte[] jsonbuff = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new UDPConnectJson() { token = token }));
			byte[] allbuff = new byte[16 + jsonbuff.Length];
			SetPackageType(allbuff, UDPPackageType.SessionPrepair);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(jsonbuff, 0, allbuff, 16, jsonbuff.Length);
			return allbuff;
		}
		static public byte[] CreateSessionConfirm(long sessionid, string token)
		{
			byte[] jsonbuff = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new UDPConnectJson() { token = token }));
			byte[] allbuff = new byte[16 + jsonbuff.Length];
			SetPackageType(allbuff, UDPPackageType.SessionConfirm);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(jsonbuff, 0, allbuff, 16, jsonbuff.Length);
			return allbuff;
		}
		static public byte[] CreateSessionReady(long sessionid, string token)
		{
			byte[] jsonbuff = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new UDPConnectJson() { token = token }));
			byte[] allbuff = new byte[16 + jsonbuff.Length];
			SetPackageType(allbuff, UDPPackageType.SessionReady);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(jsonbuff, 0, allbuff, 16, jsonbuff.Length);
			return allbuff;
		}
		static public byte[] CreateSessionError(long sessionid, string code)
		{
			byte[] jsonbuff = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new UDPConnectJson() { code = code }));
			byte[] allbuff = new byte[16 + jsonbuff.Length];
			SetPackageType(allbuff, UDPPackageType.SessionError);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(jsonbuff, 0, allbuff, 16, jsonbuff.Length);
			return allbuff;
		}
		static public byte[] CreateSessionIdle(long sessionid)
		{
			byte[] allbuff = new byte[16];
			SetPackageType(allbuff, UDPPackageType.SessionIdle);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			return allbuff;
		}

		static public byte[] CreateDataPost(long sessionid, long readminindex, long dataindex, byte[] data, int offset, int count)
		{
			byte[] allbuff = new byte[32 + count];
			SetPackageType(allbuff, UDPPackageType.DataPost);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(readminindex), 0, allbuff, 16, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(dataindex), 0, allbuff, 24, 8);
			Buffer.BlockCopy(data, offset, allbuff, 32, count);
			return allbuff;
		}
		static public void UpdateDataRead(byte[] allbuff, long readminindex)
		{
			Buffer.BlockCopy(BitConverter.GetBytes(readminindex), 0, allbuff, 16, 8);
		}
		static public byte[] CreateDataRead(long sessionid, long readminindex, long dataindex)
		{
			byte[] allbuff = new byte[32];
			SetPackageType(allbuff, UDPPackageType.DataRead);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(readminindex), 0, allbuff, 16, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(dataindex), 0, allbuff, 24, 8); ;
			return allbuff;
		}
		static public byte[] CreateDataMiss(long sessionid, long readminindex, long[] dataindexes)
		{
			byte[] allbuff = new byte[24 + dataindexes.Length * 8];
			SetPackageType(allbuff, UDPPackageType.DataMiss);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(readminindex), 0, allbuff, 16, 8);
			for (int i = 0; i < dataindexes.Length; i++)
			{
				Buffer.BlockCopy(BitConverter.GetBytes(dataindexes[i]), 0, allbuff, 24 + i * 8, 8);
			}
			return allbuff;
		}
		static public byte[] CreateDataPing(long sessionid, long readminindex, long maxdataindex)
		{
			byte[] allbuff = new byte[32];
			SetPackageType(allbuff, UDPPackageType.DataPing);
			Buffer.BlockCopy(BitConverter.GetBytes(sessionid), 0, allbuff, 8, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(readminindex), 0, allbuff, 16, 8);
			Buffer.BlockCopy(BitConverter.GetBytes(maxdataindex), 0, allbuff, 24, 8); ;
			return allbuff;
		}

		static public UDPPackageType GetPackageType(byte[] buff)
		{
			if (buff[0] == pt0 && buff[1] == pt1 && buff[2] == pt2 && buff[3] == pt3
				&& buff[4] == pt4 && buff[5] == pt5 && buff[6] == pt6)
			{
				long v = buff[7];
				return (UDPPackageType)v;
			}
			return UDPPackageType.Unknown;
		}
		static public void SetPackageType(byte[] buff, UDPPackageType type)
		{
			buff[0] = pt0; buff[1] = pt1; buff[2] = pt2; buff[3] = pt3; buff[4] = pt4; buff[5] = pt5; buff[6] = pt6;
			buff[7] = (byte)type;
		}
	}



	public abstract class UDPBaseStream : Stream
	{
		DateTime _dts = DateTime.Now;
		bool _closed = false;
		Timer _timer;

		public abstract long SessionId { get; internal set; }
		protected abstract void SendToPeer(byte[] data);
		protected abstract void OnPost(byte[] data);
		protected abstract void OnPost(byte[] data, int offset, int count);


		protected void ProcessUDPPackage(UDPPackageType pt, byte[] data)
		{
			//UDPPackageType pt = UDPMeta.GetPackageType(data);
			long sid = BitConverter.ToInt64(data, 8);

			//Console.WriteLine("ProcessUDPPackage:" + sid + ":" + pt);

			if (pt == UDPPackageType.SessionError)
			{
				Close();
			}
			else if (pt == UDPPackageType.SessionClose)
			{
				Close();
			}

			if (pt == UDPPackageType.DataPost)
			{
				if (_closed)
				{
					SendToPeer(UDPMeta.CreateSessionError(SessionId, "closed"));
					return;
				}

				long peerminreadindex = BitConverter.ToInt64(data, 16);
				lock (_postmap)
				{
					OnGetPeerMinRead(peerminreadindex);
				}

				long dataindex = BitConverter.ToInt64(data, 24);

				//Console.WriteLine("DataPost:" + dataindex);

				if (dataindex == _readminindex + 1)
				{
					OnPost(data, 32, data.Length - 32);
					_readminindex = dataindex;
					byte[] buff;
					while (_buffmap.TryGetValue(_readminindex + 1, out buff))
					{
						_readminindex++;
						_buffmap.Remove(_readminindex);
						OnPost(buff);
					}
					SendToPeer(UDPMeta.CreateDataRead(SessionId, _readminindex, dataindex));
				}
				else
				{
					if (dataindex > _readmaxindex) _readmaxindex = dataindex;
					byte[] buff = new byte[data.Length - 32];
					Buffer.BlockCopy(data, 32, buff, 0, buff.Length);
					_buffmap[dataindex] = buff;
				}

			}
			if (pt == UDPPackageType.DataRead)
			{
				long peerminreadindex = BitConverter.ToInt64(data, 16);
				long dataindex = BitConverter.ToInt64(data, 24);
				lock (_postmap)
				{
					PostMapRemove(dataindex);
					OnGetPeerMinRead(peerminreadindex);
				}
			}
			if (pt == UDPPackageType.DataMiss)
			{
				long peerminreadindex = BitConverter.ToInt64(data, 16);
				lock (_postmap)
				{
					OnGetPeerMinRead(peerminreadindex);
				}

				int misscount = (data.Length - 24) / 8;
				List<DataItem> list = null;
				for (int missid = 0; missid < misscount; missid++)
				{
					long dataindex = BitConverter.ToInt64(data, 24 + missid * 8);
					DataItem item = null;
					lock (_postmap)
					{
						_postmap.TryGetValue(dataindex, out item);
					}
					if (item != null)
					{
						if (list == null) list = new List<DataItem>();
						list.Add(item);
					}
				}
				if (list != null)
				{
					list.Sort(delegate (DataItem d1, DataItem d2)
					{
						return d1.SendTime.CompareTo(d2.SendTime);
					});
					int maxsendagain = 65536;

					foreach (DataItem item in list)
					{
						if (DateTime.Now - item.SendTime < TimeSpan.FromMilliseconds(GetPackageLostMS()))
							break;
						if (maxsendagain < item.UDPData.Length)
							break;
						maxsendagain -= item.UDPData.Length;
						UDPMeta.UpdateDataRead(item.UDPData, _readminindex);
						item.SendTime = DateTime.Now;
						item.RetryCount++;
						SendToPeer(item.UDPData);
					}
				}
			}
			if (pt == UDPPackageType.DataPing)
			{
				long peerminreadindex = BitConverter.ToInt64(data, 16);
				lock (_postmap)
				{
					OnGetPeerMinRead(peerminreadindex);
				}

				long maxdataindex = BitConverter.ToInt64(data, 24);
				List<long> misslist = null;
				for (long index = _readminindex + 1; index <= maxdataindex; index++)
				{
					if (_buffmap.ContainsKey(index))
						continue;
					if (misslist == null)
						misslist = new List<long>();
					misslist.Add(index);
					if (misslist.Count * 8 + 40 > UDPMeta.BestUDPSize)
						break;
				}
				if (misslist != null)
				{
					SendToPeer(UDPMeta.CreateDataMiss(SessionId, _readminindex, misslist.ToArray()));
				}
				else
				{
					SendToPeer(UDPMeta.CreateDataRead(SessionId, _readminindex, _readminindex));
				}
			}
		}

		long _peerminreadindex = 0;
		private void OnGetPeerMinRead(long peerminreadindex)
		{
			if (peerminreadindex < _peerminreadindex)
				return;
			if (_postmap.Count == 0)
				return;
			List<long> list = null;
			foreach (DataItem item in _postmap.Values)
			{
				if (item.DataIndex > peerminreadindex)
					continue;
				if (list == null) list = new List<long>();
				list.Add(item.DataIndex);
			}
			if (list != null)
			{
				foreach (long index in list)
					PostMapRemove(index);
			}
			_peerminreadindex = peerminreadindex;
		}

		long _readminindex = 0;
		long _readmaxindex = 0;
		Dictionary<long, byte[]> _buffmap = new Dictionary<long, byte[]>();

		public string GetDebugString()
		{
			return _buffmap.Count + ":" + _postmap.Count;
		}

		long nextdataindex = 0;
		public class DataItem
		{
			public long DataIndex;
			public byte[] UDPData;
			public DateTime SendTime;
			public int RetryCount;
			public int PingCount;

			public override string ToString()
			{
				return DataIndex + ":" + UDPData.Length;
			}
		}

		Dictionary<long, DataItem> _postmap = new Dictionary<long, DataItem>();
		int _mindelay = 0;
		int GetPackageLostMS()
		{
			int md = _mindelay == 0 ? 800 : _mindelay;
			return (int)(md * 1.3);
		}
		void PostMapRemove(long index)
		{
			DataItem item;
			if (!_postmap.TryGetValue(index, out item))
				return;
			_postmap.Remove(index);
			if (item.RetryCount == 0)
			{
				TimeSpan ts = DateTime.Now - item.SendTime;
				if (_mindelay == 0) _mindelay = (int)ts.TotalMilliseconds;
				else _mindelay = Math.Min(_mindelay, (int)ts.TotalMilliseconds);
			}
		}

		protected static void RunInNewThread(Action handler)
		{
			bool added = ThreadPool.QueueUserWorkItem(delegate
			  {
				  handler();
			  });
			if (added)
				return;
			Thread t = new Thread(delegate ()
			  {
				  handler();
			  });
			t.Start();
		}


		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			Task<int> t = null;
			Exception error = null;
			CancellationTokenSource cts = new CancellationTokenSource();
			RunInNewThread(delegate
			{
				try
				{
					t = base.ReadAsync(buffer, offset, count, cancellationToken);
				}
				catch (Exception x)
				{
					error = x;
				}
				cts.Cancel();
			});
			await cts.Token.WaitForSignalSettedAsync(-1);
			if (error != null)
				throw new Exception(error.Message, error);
			return await t;
		}

		//public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		//{
		//	return base.ReadAsync(buffer, cancellationToken);
		//}
		public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			//NOTE: new patch for the Async call , Must do this way , don't block caller thread.

			//Console.WriteLine("UDPBaseStream WriteAsync " + count);
			Exception error = null;
			CancellationTokenSource cts = new CancellationTokenSource();
			RunInNewThread(delegate
			 {
				 try
				 {
					 //Console.WriteLine("RunInNewThread WriteAsync " + count);
					 Write(buffer, offset, count);
				 }
				 catch (Exception x)
				 {
					 error = x;
				 }
				 cts.Cancel();
			 });
			await cts.Token.WaitForSignalSettedAsync(-1);
			if (error != null)
				throw new Exception(error.Message, error);
		}
		//public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		//{
		//	return base.WriteAsync(buffer, cancellationToken);
		//}

		public override void Write(byte[] buffer, int offset, int count)
		{
			if (_closed)
				throw (new Exception("stream closed"));

			//Console.WriteLine("UDPBaseStream write " + count);

			while (count > UDPMeta.BestUDPSize)
			{
				Write(buffer, offset, UDPMeta.BestUDPSize);
				offset += UDPMeta.BestUDPSize;
				count -= UDPMeta.BestUDPSize;
			}


			DataItem item = new DataItem();
			item.DataIndex = ++nextdataindex;

			item.UDPData = UDPMeta.CreateDataPost(SessionId, _readminindex, item.DataIndex, buffer, offset, count);

			lock (_postmap)
			{
				_postmap[item.DataIndex] = item;
			}

			item.SendTime = DateTime.Now;
			SendToPeer(item.UDPData);

			_timerlastitem = item;
			if (_timer == null)
			{
				_timer = new Timer(OnTimer, null, 100, 100);
			}
		}

		DataItem _timerlastitem;
		void OnTimer(object argstate)
		{
			if (_closed)
			{
				_timer.Dispose();
				return;
			}

			if (_postmap.Count == 0)
				return;

			if (DateTime.Now - _timerlastitem.SendTime < TimeSpan.FromMilliseconds(GetPackageLostMS()))
				return;


			if (_timerlastitem.PingCount > 10)
				return;

			_timerlastitem.PingCount++;

			SendToPeer(UDPMeta.CreateDataPing(SessionId, _readminindex, _timerlastitem.DataIndex));
		}

		protected void OnWorkerThreadExit()
		{
			if (_timer != null) _timer.Dispose(); _timer = null;
		}

		public override void Flush()
		{

		}


		public override void Close()
		{
			_closed = true;
			if (_timer != null) _timer.Dispose();
			base.Close();
			if (!_closesend)
			{
				_closesend = true;
				SendToPeer(UDPMeta.CreateSessionError(SessionId, "Close"));
			}
		}

		bool _closesend = false;

		#region Stream
		public override bool CanRead
		{
			get { return true; }
		}

		public override bool CanSeek
		{
			get { return false; }
		}

		public override bool CanWrite
		{
			get { return true; }
		}


		public override long Length
		{
			get { throw new NotSupportedException(); }
		}

		public override long Position
		{
			get
			{
				throw new NotSupportedException();
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}
		#endregion

	}

	public class AsyncUtil
	{
		static public CancellationTokenSource CreateAnyCancelSource(CancellationToken ct1)
		{
			CancellationTokenSource cts = new CancellationTokenSource();
			Task t1 = Task.Delay(Timeout.InfiniteTimeSpan, ct1);
			t1.ContinueWith(delegate
			{
				cts.Cancel();
			});
			return cts;
		}
		static public CancellationTokenSource CreateAnyCancelSource(params CancellationToken[] ctarr)
		{
			CancellationTokenSource cts = new CancellationTokenSource();
			Task[] ts = new Task[ctarr.Length];
			for (int i = 0; i < ctarr.Length; i++)
				ts[i] = Task.Delay(Timeout.InfiniteTimeSpan, ctarr[i]);
			Task.WhenAny(ts).ContinueWith(delegate
			{
				cts.Cancel();
			});
			return cts;
		}
		static public CancellationToken CreateAnyCancelToken(CancellationToken ct1, CancellationToken ct2)
		{
			CancellationTokenSource cts = new CancellationTokenSource();
			Task t1 = Task.Delay(Timeout.InfiniteTimeSpan, ct1);
			Task t2 = Task.Delay(Timeout.InfiniteTimeSpan, ct2);
			Task.WhenAny(t1, t2).ContinueWith(delegate
			{
				cts.Cancel();
			});
			return cts.Token;
		}
	}

	public class AsyncAwaiter
	{

		CancellationTokenSource _cts;

		public AsyncAwaiter()
		{
			_cts = new CancellationTokenSource();
		}
		public AsyncAwaiter(params CancellationToken[] tokens)
		{
			_cts = AsyncUtil.CreateAnyCancelSource(tokens);
		}


		public void Complete()
		{
			_cts.Cancel();
		}

		public bool IsCompleted
		{
			get
			{
				return _cts.IsCancellationRequested;
			}
		}

		public async Task WaitAsync()
		{
			if (_cts.IsCancellationRequested)
				return;
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
			}
			catch (OperationCanceledException)
			{
			}
		}
		public async Task WaitAsync(CancellationToken token)
		{
			if (_cts.IsCancellationRequested)
				return;
			try
			{
				token = AsyncUtil.CreateAnyCancelToken(_cts.Token, token);
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
			}
			catch (OperationCanceledException)
			{
				if (_cts.IsCancellationRequested)
					return;
				throw;
			}
		}
		public async Task<bool> WaitAsync(TimeSpan timeout)
		{
			if (_cts.IsCancellationRequested)
				return true;
			try
			{
				await Task.Delay(timeout, _cts.Token);
			}
			catch (OperationCanceledException)
			{
				return true;
			}
			return false;
		}
		public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token)
		{
			if (_cts.IsCancellationRequested)
				return true;
			try
			{
				token = AsyncUtil.CreateAnyCancelToken(_cts.Token, token);
				await Task.Delay(timeout, token);
			}
			catch (OperationCanceledException)
			{
				if (_cts.IsCancellationRequested)
					return true;
				throw;
			}
			return false;
		}
	}

	public class BufferedReader : IDisposable
	{
		public BufferedReader(CancellationToken token)
		{
			_cts = AsyncUtil.CreateAnyCancelSource(token);
			Timeout = TimeSpan.FromSeconds(55);
		}
		public BufferedReader(CancellationToken token, int capacity)
		{
			_cts = AsyncUtil.CreateAnyCancelSource(token);
			Timeout = TimeSpan.FromSeconds(55);
			Capacity = capacity;
		}

		public TimeSpan Timeout { get; set; }
		public int Capacity { get; set; }

		int _bufflen = 0;

		private bool HasCapacity(int count)
		{
			return Capacity < 1 || _bufflen <= Capacity;
		}


		byte[] _buff;
		int _bidx;
		Queue<byte[]> _packs = new Queue<byte[]>();


		public bool DataAvailable
		{
			get
			{
				return _buff != null || _packs.Count > 0;
			}
		}

		CancellationTokenSource _cts;
		AsyncAwaiter _waitfor_data;
		//AsyncAwaiter _waitfor_capacity;
		//int _nextcapacitylen = 0;


		public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			int rc = 0;

		READBUFF:

			if (_buff != null)
			{
				int rl = Math.Min(count - rc, _buff.Length - _bidx);
				Buffer.BlockCopy(_buff, _bidx, buffer, offset + rc, rl);
				rc += rl;
				_bidx += rl;
				if (_bidx == _buff.Length)
					_buff = null;

				lock (_packs)
				{
					_bufflen -= rl;
					//if (_nextcapacitylen > 0 && _waitfor_capacity != null && HasCapacity(_nextcapacitylen))
					//{
					//	_nextcapacitylen = 0;
					//	_waitfor_capacity.Complete();
					//}
				}

			}

			if (rc == count)
				return rc;

			lock (_packs)
			{
				if (_packs.Count > 0)
				{
					_buff = _packs.Dequeue();
					_bidx = 0;
					goto READBUFF;
				}

				if (rc > 0)
					return rc;//don't wait

				if (_cts.IsCancellationRequested)
					return rc;

				_cts.Token.ThrowIfCancellationRequested();
				_waitfor_data = new AsyncAwaiter();
			}

			await _waitfor_data.WaitAsync(AsyncUtil.CreateAnyCancelToken(_cts.Token, cancellationToken));

			goto READBUFF;

		}


		public void PushBuffer(byte[] buff)
		{
			_cts.Token.ThrowIfCancellationRequested();

			lock (_packs)
			{
				_packs.Enqueue(buff);
				_bufflen += buff.Length;
				if (_waitfor_data != null)
					_waitfor_data.Complete();
			}
		}
		public void PushBuffer(byte[] buffer, int offset, int count)
		{
			byte[] buff = new byte[count];
			Buffer.BlockCopy(buffer, offset, buff, 0, count);
			PushBuffer(buff);
		}



		public void Dispose()
		{
			if (_cts.IsCancellationRequested)
				return;
			lock (_packs)
			{
				if (_cts.IsCancellationRequested)
					return;
				_cts.Cancel();
				if (_waitfor_data != null) _waitfor_data.Complete();
				//if (_waitfor_capacity != null) _waitfor_capacity.Complete();

			}
		}
	}


}
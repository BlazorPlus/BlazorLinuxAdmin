using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace BlazorLinuxAdmin.TcpMaps.UDP
{


	class ClientBufferReader
	{
		ManualResetEvent _mre = new ManualResetEvent(false);

		public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
		{
			//if (_removed) throw (new InvalidOperationException());

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

				if (_removed)
					return rc;

				_mre.Reset();
			}

			_mre.WaitOne(timeout);
			goto READBUFF;
		}

		byte[] _buff;
		int _bidx;
		Queue<byte[]> _packs = new Queue<byte[]>();

		public bool DataAvailable
		{
			get
			{
				if (_buff != null) return true;
				if (_packs.Count > 0) return true;
				return false;
			}
		}

		public void OnPost(byte[] buffer, int offset, int count)
		{
			if (offset == 0 && count == buffer.Length)
			{
				OnPost((byte[])buffer.Clone());
			}
			else
			{
				byte[] pack = new byte[count];
				Buffer.BlockCopy(buffer, offset, pack, 0, count);
				OnPost(pack);
			}
		}
		public void OnPost(byte[] pack)
		{
			lock (_packs)
			{
				_packs.Enqueue(pack);
				_mre.Set();
			}
		}

		internal bool _removed = false;
		public void OnRemoved()
		{
			lock (_packs)
			{
				_removed = true;
				_mre.Set();
			}
		}
	}

	class UDPBufferReader
	{
		ManualResetEvent _mre = new ManualResetEvent(false);

		public byte[] Read(TimeSpan timeout)
		{
		//if (_removed) throw (new InvalidOperationException());

		READBUFF:

			lock (_packs)
			{
				if (_packs.Count > 0)
				{
					return _packs.Dequeue();
				}

				if (_removed)
					return null;

				_mre.Reset();
			}

			bool waited = _mre.WaitOne(timeout);
			if (!waited)
				return null;// throw (new TimeoutException());
			goto READBUFF;
		}

		Queue<byte[]> _packs = new Queue<byte[]>();

		public void OnPost(byte[] pack)
		{
			lock (_packs)
			{
				_packs.Enqueue(pack);
				_mre.Set();
			}
		}

		bool _removed = false;
		public void OnRemoved()
		{
			lock (_packs)
			{
				_removed = true;
				_mre.Set();
			}
		}
	}


	public class UDPClientListener
	{
		UdpClient _uc;
		public UDPClientListener(UdpClient uc)
		{
			_uc = uc;
			_uc.Client.ReceiveTimeout = 45000;

			_ = ReadLoopAsync();
		}

		public DateTime LastReceiveTime = DateTime.Now;


		public async Task<byte[]> ReceiveAsync()
		{
			var r = await _uc.ReceiveAsync();
			return r.Buffer;
		}


		public IPEndPoint GetLocalEndpoint()
		{
			return (IPEndPoint)_uc.Client.LocalEndPoint;
		}


		bool _closed;
		async Task ReadLoopAsync()
		{
			await Task.Yield();
			while (true)
			{
				//TODO: if not cached , and all stream closed , shutdown directly
				var data = await ReceiveAsync();
				if (data == null)
				{
					Console.WriteLine("_udp.ReceiveAsync() return null ,Close()");
					Close();
					return;
				}
				long sid = BitConverter.ToInt64(data, 8);
				if(sid==-1)
				{
					Console.WriteLine("_udp.ReceiveAsync() return " + sid + ", " + UDPMeta.GetPackageType(data));
				}
				
				UDPBufferReader reader = GetBufferReader(sid);
				reader.OnPost(data);
			}
		}

		private void Close()
		{
			_closed = true;
			lock (buffmap)
			{
				foreach (UDPBufferReader reader in buffmap.Values)
				{
					reader.OnRemoved();
				}
				buffmap.Clear();
			}
		}

		UDPBufferReader GetBufferReader(long sid)
		{
			UDPBufferReader reader;
			lock (buffmap)
			{
				if (_closed)
					throw (new Exception("closed"));
				if (!buffmap.TryGetValue(sid, out reader))
				{
					buffmap[sid] = reader = new UDPBufferReader();
				}
			}
			return reader;
		}
		Dictionary<long, UDPBufferReader> buffmap = new Dictionary<long, UDPBufferReader>();

		public byte[] Receive(long sid, TimeSpan timeout)
		{
			byte[] buff = new byte[65536];
			UDPBufferReader reader = GetBufferReader(sid);
			return reader.Read(timeout);
		}
		public void SendToServer(byte[] data, IPEndPoint _server)
		{
			_uc.Client.SendTo(data, _server);
		}

	}


	public class UDPClientStream : UDPBaseStream
	{
		static long _nextsessionid = 1;
		public override long SessionId { get; internal set; }

		UDPClientListener _udp;
		IPEndPoint _server;
		DateTime _dts = DateTime.Now;

		void CheckTimeout(TimeSpan timeout)
		{
			if (DateTime.Now - _dts > timeout)
				throw (new Exception("timeout"));
		}

		protected override void SendToPeer(byte[] data)
		{
			//Console.WriteLine("SendToPeer:" + UDPMeta.GetPackageType(data));
			_udp.SendToServer(data, _server);
		}
		protected override void OnPost(byte[] data)
		{
			_reader.OnPost(data);
		}
		protected override void OnPost(byte[] data, int offset, int count)
		{
			_reader.OnPost(data, offset, count);
		}

		private UDPClientStream()
		{

		}

		static public async Task<UDPClientStream> ConnectAsync(UDPClientListener udp, string ip, int port, TimeSpan timeout)
		{
			var s = new UDPClientStream();
			s._server = new IPEndPoint(IPAddress.Parse(ip), port);

			Exception error = null;
			CancellationTokenSource cts = new CancellationTokenSource();
			RunInNewThread(delegate
			{
				try
				{
					s._Connect(udp, ip, port, timeout);
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

			return s;
		}

		void _Connect(UDPClientListener udp, string ip, int port, TimeSpan timeout)
		{
			_udp = udp;

		StartConnect:

			SessionId = Interlocked.Increment(ref _nextsessionid);

			string guid = Guid.NewGuid().ToString();
			byte[] buffconnect = UDPMeta.CreateSessionConnect(SessionId, guid);
			SendToPeer(buffconnect); SendToPeer(buffconnect);
			while (true)
			{
				byte[] buffprepair = _udp.Receive(SessionId, TimeSpan.FromMilliseconds(500));
				if (buffprepair == null)
				{
					CheckTimeout(timeout);
					Console.WriteLine("Client StartConnect Again");
					//SendToPeer(buffconnect); SendToPeer(buffconnect);
					//continue;
					goto StartConnect;
				}
				UDPPackageType ptprepair = UDPMeta.GetPackageType(buffprepair);
				if (ptprepair != UDPPackageType.SessionPrepair)
				{
					if (ptprepair > UDPPackageType.SessionPrepair)
						goto StartConnect;
					continue;
				}
				long sidprepair = BitConverter.ToInt64(buffprepair, 8);
				if (sidprepair != SessionId)
					goto StartConnect;
				UDPConnectJson udpc = UDPConnectJson.Deserialize(Encoding.UTF8.GetString(buffprepair, 16, buffprepair.Length - 16));
				if (udpc.token != guid)
					goto StartConnect;
				break;
			}
			byte[] buffconfirm = UDPMeta.CreateSessionConfirm(SessionId, guid);
			SendToPeer(buffconfirm); SendToPeer(buffconfirm);
			while (true)
			{
				byte[] buffready = _udp.Receive(SessionId, TimeSpan.FromMilliseconds(500));
				if (buffready == null)
				{
					CheckTimeout(timeout);
					SendToPeer(buffconfirm); SendToPeer(buffconfirm);
					continue;
				}
				UDPPackageType ptprepair = UDPMeta.GetPackageType(buffready);
				if (ptprepair != UDPPackageType.SessionReady)
				{
					if (ptprepair > UDPPackageType.SessionReady)
						goto StartConnect;
					continue;
				}
				long sidprepair = BitConverter.ToInt64(buffready, 8);
				if (sidprepair != SessionId)
					goto StartConnect;
				UDPConnectJson udpc = UDPConnectJson.Deserialize(Encoding.UTF8.GetString(buffready, 16, buffready.Length - 16));
				if (udpc.token != guid)
					goto StartConnect;
				break;
			}

			_reader = new ClientBufferReader();
			Thread t = new Thread(ClientWorkThread);//TODO:Convert to async
			t.IsBackground = true;
			t.Start();
		}

		ClientBufferReader _reader;


		void ClientWorkThread()
		{
			try
			{
				while (true)
				{
					//TODO:when timeout , try to send idle message
					byte[] data = _udp.Receive(SessionId, TimeSpan.FromSeconds(40));
					if (data == null)
					{
						if (!_reader._removed)
							Console.WriteLine("UDPClientStream.ClientWorkThread.Timeout-40");

						_reader.OnRemoved();

						base.OnWorkerThreadExit();

						return;
					}

					try
					{
						UDPPackageType pt = UDPMeta.GetPackageType(data);
						ProcessUDPPackage(pt, data);
					}
					catch (Exception x)
					{
						TcpMapService.OnError(x);
					}
				}
			}
			catch (Exception x)
			{
				TcpMapService.OnError(x);
			}

		}


		public override int Read(byte[] buffer, int offset, int count)
		{
			int rc = _reader.Read(buffer, offset, count, TimeSpan.FromSeconds(55));
			//Console.WriteLine("UDPClientStream read " + rc);
			return rc;
		}

		public override void Flush()
		{

		}
		public override void Close()
		{
			if (_reader != null) _reader.OnRemoved();
			base.Close();
		}



	}



}
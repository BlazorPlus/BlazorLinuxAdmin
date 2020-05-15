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

	public interface IUDPServer
	{
		void SendToClient(IPEndPoint remote, byte[] buff);
		byte[] Receive(TimeSpan timeout, out IPEndPoint remote);
		IPEndPoint LocalEndPoint { get; }
	}


	public class UDPServerListener : IDisposable
	{
		IUDPServer _udp;
		Action<Stream, IPEndPoint> _onstream;
		public UDPServerListener(IUDPServer udp, Action<Stream, IPEndPoint> onstream)
		{
			_udp = udp;
			_onstream = onstream;

			_workthread = new Thread(ListenerWorkThread);//TODO:Convert to async
			_workthread.IsBackground = true;
			_workthread.Start();
			//_timer = new Timer(delegate
			//{
			//	OnTimer();
			//}, null, 20, 20);
		}

		Thread _workthread;

		//Timer _timer;
		//void OnTimer()
		//{
		//}

		Dictionary<long, ServerStream> strmap = new Dictionary<long, ServerStream>();

		void ListenerWorkThread()
		{
			while (this._workthread != null)
			{
				try
				{
					//TODO: if not cached , and all stream closed , shutdown directly

					IPEndPoint ep;
					byte[] data = _udp.Receive(TimeSpan.FromSeconds(90), out ep);
					if (data == null)
					{
						Console.WriteLine("_udp.Receive return null for a long time...; Shutdown..."+this._udp.LocalEndPoint);
						this.Close();
						return;
					}

					UDPPackageType pt = UDPMeta.GetPackageType(data);
					if (pt == UDPPackageType.Unknown)
					{
						//Console.WriteLine("_udp.Receive return Unknown;");
						return;
					}

					long sid = BitConverter.ToInt64(data, 8);
					ServerStream ss;

					//Console.WriteLine("_udp.Receive " + sid + ":" + pt);

					if (pt == UDPPackageType.SessionConnect)
					{
						UDPConnectJson udpc = UDPConnectJson.Deserialize(Encoding.UTF8.GetString(data, 16, data.Length - 16));
						lock (strmap)
						{
							if (!strmap.TryGetValue(sid, out ss))
							{
								ss = new ServerStream(this, sid, ep, udpc);
								strmap[sid] = ss;
							}
						}
						if (ss.ConnectToken != udpc.token)
						{
							ss.ForceClose();
							lock (strmap)
							{
								ss = new ServerStream(this, sid, ep, udpc);
								strmap[sid] = ss;
							}
						}
						else
						{
							ss.Process(pt, data);
						}
					}
					else
					{
						lock (strmap)
						{
							strmap.TryGetValue(sid, out ss);
						}
						if (ss != null)
						{
							ss.Process(pt, data);
						}
						else
						{
							_udp.SendToClient(ep, UDPMeta.CreateSessionError(sid, "NotFound"));
						}
					}
				}
				catch (ThreadAbortException)
				{
					break;
				}
				catch (Exception x)
				{
					TcpMapService.OnError(x);
				}
			}
		}

		void RejectConnect(ServerStream stream)
		{
			long sid = stream.SessionId;
			bool removed = false;
			lock (strmap)
			{
				ServerStream ss;
				if (strmap.TryGetValue(sid, out ss))
				{
					if (ss == stream)
					{
						strmap.Remove(sid);
						removed = true;
					}
				}
			}
			if (removed)
			{
				_udp.SendToClient(stream.RemoteEndPoint, UDPMeta.CreateSessionError(sid, "Reject"));
			}
		}

		public class ServerStream : UDPBaseStream
		{
			public override long SessionId { get; internal set; }
			public IPEndPoint RemoteEndPoint { get; private set; }

			UDPServerListener _sl;
			CancellationTokenSource cts = new CancellationTokenSource();
			BufferedReader _reader;

			public ServerStream(UDPServerListener sl, long sid, IPEndPoint ep, UDPConnectJson cjson)
			{
				_sl = sl;
				SessionId = sid;
				RemoteEndPoint = ep;
				ConnectToken = cjson.token;
			}



			UDPPackageType _waitfor = UDPPackageType.SessionConnect;
			public string ConnectToken { get; private set; }

			protected override void SendToPeer(byte[] data)
			{
				//Console.WriteLine("SendToPeer:" + UDPMeta.GetPackageType(data));
				_sl._udp.SendToClient(RemoteEndPoint, data);
			}
			protected override void OnPost(byte[] data)
			{
				_reader.PushBuffer(data);
			}
			protected override void OnPost(byte[] data, int offset, int count)
			{
				_reader.PushBuffer(data, offset, count);
			}

			public void Process(UDPPackageType pt, byte[] data)
			{
				if (_waitfor == pt)
				{
					TcpMapService.LogMessage("UDPServer:" + pt);

					if (pt == UDPPackageType.SessionConnect)
					{
						UDPConnectJson udpc = UDPConnectJson.Deserialize(Encoding.UTF8.GetString(data, 16, data.Length - 16));
						if (udpc.token != this.ConnectToken)
						{
							_sl.RejectConnect(this);
							return;
						}
						byte[] buffprepair = UDPMeta.CreateSessionPrepair(SessionId, ConnectToken);
						SendToPeer(buffprepair); SendToPeer(buffprepair);
						_waitfor = UDPPackageType.SessionConfirm;
						return;
					}
					if (pt == UDPPackageType.SessionConfirm)
					{
						UDPConnectJson udpc = UDPConnectJson.Deserialize(Encoding.UTF8.GetString(data, 16, data.Length - 16));
						if (udpc.token != this.ConnectToken)
						{
							_sl.RejectConnect(this);
							return;
						}
						byte[] buffready = UDPMeta.CreateSessionReady(SessionId, ConnectToken);
						SendToPeer(buffready); SendToPeer(buffready);
						_waitfor = UDPPackageType.SessionIdle;
						_reader = new BufferedReader(cts.Token);
						ThreadPool.QueueUserWorkItem(delegate
						{
							_sl._onstream(this, RemoteEndPoint);
						});
						return;
					}
					if (pt == UDPPackageType.SessionIdle)
					{
						SendToPeer(UDPMeta.CreateSessionIdle(SessionId));
						return;
					}
				}
				else
				{
					ProcessUDPPackage(pt, data);
				}
			}

			public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				int rc = await _reader.ReadAsync(buffer, offset, count, cancellationToken);
				//Console.WriteLine("UDPServerStream read " + rc);
				return rc;

			}
			public override int Read(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}



			public override void Close()
			{
				cts.Cancel();
				if (_reader != null) _reader.Dispose();
				base.Close();
			}

			public void ForceClose()
			{
				Close();
			}

		}


		public void Close()
		{
			if (_workthread != null)
			{
				//NOT SUPPORT ON THIS PLATFORM
				//workthread.Abort();
				_workthread = null;
			}
		}

		public void Dispose()
		{
			Close();
		}
	}

}
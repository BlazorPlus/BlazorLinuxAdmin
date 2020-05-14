using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Security.Policy;

namespace BlazorLinuxAdmin.TcpMaps
{
	public class TcpMapBaseWorker
	{

		public bool IsStarted { get; protected set; }
		public Exception Error { get; set; }

		public void OnError(Exception x)
		{
			Error = x;
			TcpMapService.OnError(x);
		}

		public ConcurrentQueue<string> LogMessages = new ConcurrentQueue<string>();

		public void LogMessage(string msg)
		{
			LogMessages.Enqueue(msg);
			while (LogMessages.Count > 200)
				LogMessages.TryDequeue(out _);
			TcpMapService.LogMessage(msg);
		}
	}



	[Serializable]
	public class TcpMapLicense
	{
		static public TcpMapLicense CreateNew(string key, string name)
		{
			TcpMapLicense lic = new TcpMapLicense() { Key = key, Name = name };
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
			lic.RSAXmlData = rsa.ToXmlString(true);
			return lic;
		}

		public string Key { get; set; }
		public string Name { get; set; }
		public string RSAXmlData { get; set; }

		public void Validate()
		{
			if (string.IsNullOrEmpty(Key)) throw new Exception("Miss Key");
			if (string.IsNullOrEmpty(Name)) throw new Exception("Miss Name");
			if (string.IsNullOrEmpty(RSAXmlData)) throw new Exception("Miss RSAXmlData");
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
			rsa.FromXmlString(RSAXmlData);
		}

		public TcpMapLicense Clone()
		{
			return new TcpMapLicense() { Key = Key, Name = Name, RSAXmlData = RSAXmlData };
		}


		public byte[] ComputeHash(byte[] data)
		{
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
			rsa.FromXmlString(RSAXmlData);
			return rsa.SignHash(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}
		public byte[] EncryptData(byte[] data)
		{
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
			rsa.FromXmlString(RSAXmlData);
			return rsa.Encrypt(data, false);
		}

		public void GenerateSecureKeyAndHash(out byte[] sourceKeyIV, out byte[] encryptedkeyandiv, out byte[] sourcekeyhash)
		{
			Random r = new Random(Guid.NewGuid().GetHashCode());
			byte[] keyandiv = new byte[32];//key=24,iv=8
			r.NextBytes(keyandiv);
			EncryptSourceKey(keyandiv, out encryptedkeyandiv, out sourcekeyhash);
			sourceKeyIV = keyandiv;
		}

		private void EncryptSourceKey(byte[] keyandiv, out byte[] encryptedkeyandiv, out byte[] sourcekeyhash)
		{
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
			rsa.FromXmlString(RSAXmlData);
			encryptedkeyandiv = rsa.Encrypt(keyandiv, false);
			sourcekeyhash = rsa.SignHash(keyandiv, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}

		public void DescriptSourceKey(byte[] encryptedkeyandiv, byte[] sourcekeyhash, out byte[] keyandiv)
		{
			RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
			rsa.FromXmlString(RSAXmlData);
			byte[] srcdata = rsa.Decrypt(encryptedkeyandiv, false);
			byte[] srchash = rsa.SignHash(srcdata, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			if (srcdata.Length != 32)
				throw new Exception("size not match");
			if (!srchash.SequenceEqual(sourcekeyhash))
				throw new Exception("hash not match");
			keyandiv = srcdata;
		}

		public void OverrideStream(Stream stream, byte[] keyIV, out Stream sread, out Stream swrite)
		{
			throw new NotSupportedException();
			//swrite = stream;
			//sread = stream;

			//TODO: not flush as expected , do it later.

			//var tdes = TripleDES.Create();
			//tdes.Key = keyIV.AsSpan().Slice(0, 24).ToArray();
			//tdes.IV = keyIV.AsSpan().Slice(24, 8).ToArray();
			//sread = new CryptoStream(stream, tdes.CreateDecryptor(), CryptoStreamMode.Read);
			//swrite = new CryptoStream(stream, tdes.CreateEncryptor(), CryptoStreamMode.Write);
		}


	}


	[Serializable]
	public class TcpMapClient
	{
		public TcpMapLicense License { get; set; }

		public string Id { get; set; }

		public string ServerHost { get; set; }
		public int ServerPort { get; set; }    //server mapped port

		public string ClientHost { get; set; }

		public int ClientPort { get; set; }

		public int ProxyRoutePort { get; set; }    //

		public bool IsDisabled { get; set; }

		public int PreSessionCount { get; set; }


		public string Comment { get; set; }

		public TcpMapClient Clone()
		{
			var newinst = (TcpMapClient)this.MemberwiseClone();
			newinst.License = License.Clone();
			return newinst;
		}
	}



	[Serializable]
	public class TcpMapServer
	{
		public TcpMapLicense License { get; set; }

		public string Id { get; set; }

		public string Comment { get; set; }


		public string ServerBind { get; set; } = "0.0.0.0";

		public int ServerPort { get; set; }

		public bool IsDisabled { get; set; }

		public bool IsValidated { get; set; }

		public string IPServiceUrl { get; set; }

		public bool AllowConnector { get; set; }

		public TcpMapLicense ConnectorLicense { get; set; }

		public TcpMapServer Clone()
		{
			var newinst = (TcpMapServer)this.MemberwiseClone();
			newinst.License = License.Clone();
			newinst.ConnectorLicense = ConnectorLicense?.Clone();
			return newinst;
		}
	}


	// map local machine 0.0.0.0:LocalPort to ServerHost:ServerPort , to ClientHost:ClientPort
	// if the client Provide ProxyRoutePort/UDP , will use 0.0.0.0:LocalPort to ProxyRoutePort/UDP to ClientHost:ClientPort
	// the server will save bandwidth cost
	[Serializable]
	public class TcpMapConnector
	{
		public TcpMapLicense License { get; set; }

		public string Id { get; set; }

		public string Comment { get; set; }

		public string LocalBind { get; set; } = "0.0.0.0";

		public int LocalPort { get; set; }      //port of localhost

		public string ServerHost { get; set; }

		public int ServerPort { get; set; }     //server mapped port

		public bool IsDisabled { get; set; }

		//Default is false , at most case the ProxyRoutePort/UDP shall works
		public bool UseServerBandwidth { get; set; }

		public bool UseProxyPort { get; set; }

		public bool UseUDP { get; set; }

		public TcpMapConnector Clone()
		{
			var newinst = (TcpMapConnector)this.MemberwiseClone();
			return newinst;
		}
	}

	public class CommandMessage
	{
		public const int MAX_PACKAGE_SIZE = 1024 * 1024;

		public string Name { get; set; }
		public Memory<byte> Data { get; set; }
		public string[] Args { get; set; }

		public CommandMessage() { }
		public CommandMessage(string name, params string[] args) { Name = name; Args = args; }

		public override string ToString()
		{
			if (Args == null)
				return Name;
			return Name + ":" + string.Join(",", Args);
		}


		const string STR_H8 = "CMDMSGv1";
		const string END_H8 = "ENDMSGv1";

		static public async Task<CommandMessage> ReadFromSocketAsync(Socket socket)
		{
			async Task<int> ReadFunc(byte[] buffer, int offset, int length)
			{
				ArraySegment<byte> seg = new ArraySegment<byte>(buffer, offset, length);
				return await socket.ReceiveAsync(seg, SocketFlags.None);//SocketFlags.Partial not OK in Linu
			}
			return await ReadAsync(ReadFunc);
		}
		static public async Task<CommandMessage> ReadFromStreamAsync(Stream stream)
		{
			async Task<int> ReadFunc(byte[] buffer, int offset, int length)
			{
				return await stream.ReadAsync(buffer, offset, length);
			}
			return await ReadAsync(ReadFunc);
		}
		static public async Task<CommandMessage> ReadAsync(Func<byte[], int, int, Task<int>> readFunc)
		{

			byte[] header = new byte[12];
			int hcount = 0;
			while (hcount < 12)
			{
				int rc = await readFunc(header, hcount, 12 - hcount);
				if (rc == 0)
				{
					if (hcount == 0)
					{
						//TcpMapService.LogMessage("ReadFromStreamAsync read 0 bytes "+Environment.StackTrace);
						return null;//client maybe quited.
					}
					throw new Exception("Unexpected END for header");
				}
				hcount += rc;
			}
			string h8 = System.Text.Encoding.ASCII.GetString(header, 0, 8);
			if (STR_H8 != h8)
				throw new Exception("Invalid header : " + h8);
			uint size = BitConverter.ToUInt32(header, 8);
			if (size > MAX_PACKAGE_SIZE)
				throw new Exception("reach MAX_PACKAGE_SIZE:" + size);
			byte[] buffer = new byte[size - 12];
			int bytecount = 0;
			while (bytecount < buffer.Length)
			{
				int rc = await readFunc(buffer, bytecount, buffer.Length - bytecount);
				if (rc == 0)
					throw new Exception("Unexpected END for message");
				bytecount += rc;
			}
			return UnpackRest(new MemoryStream(buffer));
		}

		static public CommandMessage Unpack(MemoryStream ms)
		{
			byte[] header = new byte[12];
			ms.Read(header, 0, 12);
			string h8 = System.Text.Encoding.ASCII.GetString(header, 0, 8);
			if (STR_H8 != h8)
				throw new Exception("Invalid header : " + h8);
			uint size = BitConverter.ToUInt32(header, 8);
			if (size > MAX_PACKAGE_SIZE)
				throw new Exception("reach MAX_PACKAGE_SIZE:" + size);
			return UnpackRest(ms);
		}

		static CommandMessage UnpackRest(MemoryStream ms)
		{
			CommandMessage msg = new CommandMessage();

			BinaryReader br = new BinaryReader(ms); //TODO:performance dont use BinaryWriter/BinaryReader
			string flag = br.ReadString();
			if (flag[0] == '1')
				msg.Name = br.ReadString();
			if (flag[1] == '1')
			{
				msg.Data = br.ReadBytes(br.ReadInt32());//TODO:..better implementation for Memory<byte>
			}
			if (flag[2] == '1')
			{
				string parts = br.ReadString();
				if (parts == "")
				{
					msg.Args = Array.Empty<string>();
				}
				else
				{
					msg.Args = parts.Split(',');
					for (int i = 0; i < msg.Args.Length; i++)
					{
						msg.Args[i] = msg.Args[i] == "-" ? null : br.ReadString();
						//if (msg.Args[i] == END_H8) throw new Exception("unexpected " + END_H8 + " parts:" + parts);
					}
				}
			}

			string endstr = br.ReadString();
			if (endstr != END_H8)
				throw new Exception("Invalid footer : " + endstr);

			return msg;
		}

		public byte[] Pack()    //TODO:performance return Memory<byte>
		{
			int capacity = 64;
			if (Name != null) capacity += Name.Length * 2;
			if (!Data.IsEmpty) capacity += Data.Length;
			if (Args != null) capacity += Args.Length * 8 + Args.Sum(v => v?.Length * 2 ?? 0);

			MemoryStream ms = new MemoryStream(capacity);//TODO:performance dont use MemoryStream
			BinaryWriter br = new BinaryWriter(ms); //TODO:performance dont use BinaryWriter/BinaryReader
			byte[] h8 = System.Text.Encoding.ASCII.GetBytes(STR_H8);
			br.Write(h8);
			br.Write(0);//place holder
			br.Write((Name == null ? "0" : "1") + (Data.IsEmpty ? "0" : "1") + (Args == null ? "0" : "1"));
			if (Name != null) br.Write(Name);
			if (!Data.IsEmpty)
			{
				br.Write((int)Data.Length);
				br.Write(Data.Span);
			}
			if (Args != null)
			{
				br.Write(string.Join(",", Args.Select(v => v == null ? "-" : v.Length.ToString()).ToList()));//ToArray , compiler bug on my PC
				foreach (string arg in Args)
				{
					if (arg != null)
						br.Write(arg);
				}
			}
			br.Write(END_H8);

			byte[] data = ms.ToArray();
			if (data.Length > MAX_PACKAGE_SIZE)
				throw new Exception("reach MAX_PACKAGE_SIZE:" + data.Length);
			byte[] bsiv = BitConverter.GetBytes((uint)data.Length);
			Buffer.BlockCopy(bsiv, 0, data, 8, 4);

			if(data.Length>capacity)
				Console.WriteLine("capacity :" + data.Length + "/" + capacity);

			return data;
		}

	}

}

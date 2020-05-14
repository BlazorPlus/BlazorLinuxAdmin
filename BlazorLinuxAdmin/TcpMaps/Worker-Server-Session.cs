using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Protocol;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;


namespace BlazorLinuxAdmin.TcpMaps
{


	class TcpMapServerSession : TcpMapBaseSession
	{
		Stream _stream;
		//string _sid;
		public TcpMapServerSession(Stream stream, string sid)
		{
			_stream = stream;
			//_sid = sid;
		}

		CancellationTokenSource cts_wait_work = new CancellationTokenSource();

		public async Task WorkAsync()
		{
			try
			{
				await WorkAsync(_stream);
			}
			finally
			{
				cts_wait_work.Cancel();
			}
		}

		Stream _sread;
		Stream _swrite;

		CancellationTokenSource cts_wait_attach = new CancellationTokenSource();
		async Task WaitForAttachAsync()
		{
			for (int i = 0; i < 100; i++)
			{
				if (_sread != null && _swrite != null)
					return;
				if (await cts_wait_attach.Token.WaitForSignalSettedAsync(100))
				{
					Debug.Assert(_sread != null && _swrite != null);
					return;
				}
			}
			throw new Exception("timeout");
		}

		protected override async Task<CommandMessage> ReadMessageAsync()
		{
			if (_sread == null) await WaitForAttachAsync();
			ReadAgain:
			var msg = await CommandMessage.ReadFromStreamAsync(_sread);
			if (msg == null || msg.Name == "data")
				return msg;

			//TcpMapService.LogMessage("ServerSession:get message " + msg);

			switch (msg.Name)
			{
				case "_ping_":
					await _swrite.WriteAsync(new CommandMessage("_ping_result_").Pack());
					break;
				case "_ping_result_":
					break;
				default:
					TcpMapService.LogMessage("Error: 3 Ignore message " + msg);
					break;
			}
			goto ReadAgain;
		}

		protected override async Task WriteMessageAsync(CommandMessage msg)
		{
			if (_swrite == null) await WaitForAttachAsync();
			await _swrite.WriteAsync(msg.Pack());
		}

		public async Task UseThisSocketAsync(Stream sread, Stream swrite)
		{
			_sread = sread;
			_swrite = swrite;
			cts_wait_attach.Cancel();
			await cts_wait_work.Token.WaitForSignalSettedAsync(-1);
		}


	}
}

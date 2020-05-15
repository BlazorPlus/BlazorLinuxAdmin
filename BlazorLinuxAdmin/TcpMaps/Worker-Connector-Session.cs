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
	class TcpMapConnectorSession : TcpMapBaseSession
	{
		Stream _stream;

		public TcpMapConnectorSession(Stream stream)
		{
			_stream = stream;
		}

		public async Task WorkAsync()
		{
			await WorkAsync(_stream);
		}

		Stream _sread;
		Stream _swrite;

		protected override async Task<CommandMessage> ReadMessageAsync()
		{
			ReadAgain:

			CommandMessage msg;
			var cts = new CancellationTokenSource();
			_ = Task.Run(async delegate
			{
				if (await cts.Token.WaitForSignalSettedAsync(16000))
					return;
				try
				{
					await _swrite.WriteAsync(new CommandMessage("_ping_", "forread").Pack());
				}
				catch (Exception x)
				{
					OnError(x);
				}
			});
			try
			{
				msg = await CommandMessage.ReadFromStreamAsync(_sread);
			}
			finally
			{
				cts.Cancel();
			}

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
			await _swrite.WriteAsync(msg.Pack());
		}


		public async Task DirectWorkAsync(Stream sread,Stream swrite)
		{
			_sread = sread;
			_swrite = swrite;
			await WorkAsync();
		}
	}
}

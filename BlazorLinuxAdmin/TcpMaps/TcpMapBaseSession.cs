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
	public abstract class TcpMapBaseSession : TcpMapBaseWorker
	{
		protected async Task WorkAsync(Stream stream)
		{
			if (stream == null) throw new ArgumentException("stream");
			var t1 = StreamToSesionAsync(stream);
			var t2 = SessionToStreamAsync(stream);
			await Task.WhenAny(t1, t2);
		}

		async Task StreamToSesionAsync(Stream stream)
		{
			try
			{
				byte[] buffer = new byte[TcpMapService.DefaultBufferSize];//TODO:const
				while (true)
				{
					//TcpMapService.LogMessage(this.GetType().Name + " ReadAsync");
					int rc = await stream.ReadAsync(buffer, 0, buffer.Length);
					//TcpMapService.LogMessage(this.GetType().Name + " ReadAsync DONE : " + rc);
					if (rc == 0)
						return;
					CommandMessage msg = new CommandMessage();
					msg.Name = "data";
					msg.Data = new Memory<byte>(buffer, 0, rc);
					await WriteMessageAsync(msg);
					//TcpMapService.LogMessage(this.GetType().Name + " WriteMessageAsync DONE : " + msg);
				}
			}
			catch (Exception x)
			{
				//TcpMapService.OnError(x);
			}
			finally
			{
				//TcpMapService.LogMessage(this.GetType().Name + " EXITING StreamToSesionAsync");
			}
		}
		async Task SessionToStreamAsync(Stream stream)
		{
			try
			{
				while (true)
				{
					//TcpMapService.LogMessage(this.GetType().Name + " ReadMessageAsync");
					var msg = await ReadMessageAsync();
					//TcpMapService.LogMessage(this.GetType().Name + " ReadMessageAsync DONE : " + msg);
					if (msg == null)
						return;
					if (msg.Name == "data")
					{
						await stream.WriteAsync(msg.Data);
					}
					else
					{
						TcpMapService.LogMessage("Warning:this message shall not pass to BaseSession : " + msg);
					}
					//TcpMapService.LogMessage(this.GetType().Name + " WriteAsync DONE : " + msg);
				}
			}
			catch (Exception x)
			{
				//TcpMapService.OnError(x);
			}
			finally
			{
				//TcpMapService.LogMessage(this.GetType().Name + " EXITING SessionToStreamAsync");
			}
		}

		protected abstract Task<CommandMessage> ReadMessageAsync();
		protected abstract Task WriteMessageAsync(CommandMessage msg);

	}

}

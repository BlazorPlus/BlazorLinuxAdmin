using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlazorLinuxAdmin.TcpMaps.Crypto
{
	public abstract class BlockCryptoStream : Stream
	{
		const int MAX_BLOCK_SIZE = 1024 * 256;

		protected Stream _inner;
		protected TripleDES _tdes;

		static public BlockCryptoStream CreateEncryptWriter(Stream stream, TripleDES tdes)
		{
			var writer = new Writer();
			writer._inner = stream;
			writer._tdes = tdes;
			return writer;
		}
		class Writer : BlockCryptoStream
		{
			public override bool CanWrite => true;
			public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				if (count > MAX_BLOCK_SIZE)
				{
					for (int start = 0; start < count; start += MAX_BLOCK_SIZE)
					{
						int len = Math.Min(MAX_BLOCK_SIZE, count - start);
						await WriteAsync(buffer, offset + start, len, cancellationToken);
					}
					return;
				}

				byte[] outputdata = new byte[count + 32];
				int outputlen = 0;
				using (MemoryStream bs = new MemoryStream(outputdata))
				{
					byte[] cntbytes = BitConverter.GetBytes((int)count);
					bs.Write(cntbytes);
					Array.Reverse(cntbytes);
					bs.Write(cntbytes);
					bs.Write(cntbytes);//use for rest size

					using var trans = _tdes.CreateEncryptor();
					using (CryptoStream cs = new CryptoStream(bs, trans, CryptoStreamMode.Write))
					{
						cs.Write(cntbytes);//prefix 
						cs.Write(buffer, offset, count);
						Array.Reverse(cntbytes);
						cs.FlushFinalBlock();

						outputlen = (int)bs.Position;
					}

				}

				Buffer.BlockCopy(BitConverter.GetBytes((int)outputlen), 0, outputdata, 8, 4);

				//Console.WriteLine("MemoryStream : " + outputdata.Length + " , " + outputlen + " , " + count);

				//var msg = await CommandMessage.ReadFromStreamAsync(new MemoryStream(buffer, offset, count));
				//Console.WriteLine("MemoryStream : " + msg);

				//BlockCryptoStream test = CreateDecryptReader(new MemoryStream(outputdata, 0, outputlen), _tdes);
				//byte[] testdata = new byte[count];
				//int rc = await test.ReadAsync(testdata, 0, testdata.Length);
				//Console.WriteLine("MemoryStream TESTED : " + count + "/" + rc + " : " + testdata.SequenceEqual(new Memory<byte>(buffer, offset, count).ToArray()));

				//Console.WriteLine("MemoryStream : " + BitConverter.ToString(outputdata, 0, outputlen));

				await _inner.WriteAsync(outputdata, 0, outputlen, cancellationToken);
			}

		}

		static public BlockCryptoStream CreateDecryptReader(Stream stream, TripleDES tdes)
		{
			var reader = new Reader();
			reader._inner = stream;
			reader._tdes = tdes;
			return reader;
		}
		class Reader : BlockCryptoStream
		{
			public override bool CanRead => true;

			int _blockoffset;
			byte[] _blockbytes;

			int _blocktimes = 0;
			int _totalrc;
			int _totalrc2;

			public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				if (_blockbytes == null)
				{
					_blocktimes++;

					byte[] header = new byte[12];
					int hcount = 0;
					while (hcount < 12)
					{
						int hrc = await _inner.ReadAsync(header, hcount, 12 - hcount);
						if (hrc == 0)
						{
							if (hcount == 0)
								return 0;
							throw new Exception("Unexpected END");
						}
						hcount += hrc;
						_totalrc2 += hrc;
					}

					int size = BitConverter.ToInt32(header);

					if (header[0] != header[7] || header[1] != header[6] || header[2] != header[5] || header[3] != header[4])
					{
						if (System.Text.Encoding.ASCII.GetString(header) == CommandMessage.STR_H8)
						{
							byte[] sizebytes = new byte[4];
							int hrc = await _inner.ReadAsync(sizebytes, 0, 4);
							if (hrc != 4) throw new Exception("test failed");
							int packsize = BitConverter.ToInt32(sizebytes);
							byte[] packdata = new byte[packsize];
							int packstart = 0;
							while (packstart < packsize)
							{
								hrc = await _inner.ReadAsync(packdata, packstart, packsize - packstart);
								if (hrc == 0)
									throw new Exception("test failed");
								packstart += hrc;
							}
							Console.WriteLine("Not Encrypted Data : " + CommandMessage.UnpackRest(new MemoryStream(packdata)));
						}

						throw new Exception("Invalid size header #" + _blocktimes + " , " + size + " , " + _totalrc);
					}


					int totalsize = BitConverter.ToInt32(header, 8);


					if (size < 1 || size > MAX_BLOCK_SIZE)
						throw new Exception("Invalid size : #" + _blocktimes + " , " + size + " , " + _totalrc);

					byte[] rawBlock = new byte[totalsize - 12];
					int rawStart = 0;
					while (rawStart < rawBlock.Length)
					{
						int hrc = await _inner.ReadAsync(rawBlock, rawStart, rawBlock.Length - rawStart, cancellationToken);
						if (hrc == 0)
							throw new Exception("Unexpected END");
						rawStart += hrc;
					}

					_blockbytes = new byte[size + 4];//4 for prefix
					MemoryStream msout = new MemoryStream(_blockbytes);
					using var trans = _tdes.CreateDecryptor();
					using (CryptoStream cs = new CryptoStream(new MemoryStream(rawBlock), trans, CryptoStreamMode.Read))
					{
						cs.CopyTo(msout);
						Debug.Assert(msout.Position == _blockbytes.Length);
					}

					if (header[0] != _blockbytes[3] || header[1] != _blockbytes[2] || header[2] != _blockbytes[1] || header[3] != _blockbytes[0])
						throw new Exception("Invalid size prefix #" + _blocktimes + " , " + size + " , " + _totalrc);

					_blockoffset = 4;

				}

				int maxrc = Math.Min(count, _blockbytes.Length - _blockoffset);

				Buffer.BlockCopy(_blockbytes, _blockoffset, buffer, offset, maxrc);

				_blockoffset += maxrc;

				_totalrc += maxrc;
				_totalrc2 += maxrc;

				//Console.WriteLine("Readed : #" + _blocktimes + " , " + maxrc + "/" + count + " , " + _blockoffset + "/" + _blockbytes.Length + " , " + _totalrc + "/" + _totalrc2);

				if (_blockoffset == _blockbytes.Length)
					_blockbytes = null;

				return maxrc;

			}
		}



		#region BasicStreamOverride


		public override void Flush()
		{

		}
		public override Task FlushAsync(CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new InvalidOperationException();

		public override long Position { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }


		public override int Read(byte[] buffer, int offset, int count)
		{
			throw new InvalidOperationException();
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new InvalidOperationException();
		}

		public override void SetLength(long value)
		{
			throw new InvalidOperationException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new InvalidOperationException();
		}

		#endregion

	}
}

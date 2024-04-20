﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMWD.Protocols.Modbus.Common;
using AMWD.Protocols.Modbus.Common.Contracts;
using AMWD.Protocols.Modbus.Common.Protocols;

namespace AMWD.Protocols.Modbus.Proxy
{
	/// <summary>
	/// Implements a Modbus TCP server proxying all requests to a Modbus client of choice.
	/// </summary>
	public class ModbusTcpProxy : IDisposable
	{
		#region Fields

		private bool _isDisposed;

		private TcpListener _listener;
		private CancellationTokenSource _stopCts;
		private Task _clientConnectTask = Task.CompletedTask;

		private readonly SemaphoreSlim _clientListLock = new(1, 1);
		private readonly List<TcpClient> _clients = [];
		private readonly List<Task> _clientTasks = [];

		#endregion Fields

		#region Constructors

		/// <summary>
		/// Initializes a new instance of the <see cref="ModbusTcpProxy"/> class.
		/// </summary>
		/// <param name="client">The <see cref="ModbusClientBase"/> used to request the remote device, that should be proxied.</param>
		/// <param name="listenAddress">An <see cref="IPAddress"/> to listen on (Default: <see cref="IPAddress.Loopback"/>).</param>
		/// <param name="listenPort">A port to listen on (Default: 502).</param>
		public ModbusTcpProxy(ModbusClientBase client, IPAddress listenAddress = null, int listenPort = 502)
		{
			Client = client ?? throw new ArgumentNullException(nameof(client));

			ListenAddress = listenAddress ?? IPAddress.Loopback;

			if (ushort.MinValue < listenPort || listenPort < ushort.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(listenPort));

			try
			{
#if NET8_0_OR_GREATER
				using var testListener = new TcpListener(ListenAddress, listenPort);
#else
				var testListener = new TcpListener(ListenAddress, listenPort);
#endif
				testListener.Start(1);
				ListenPort = (testListener.LocalEndpoint as IPEndPoint).Port;
				testListener.Stop();
			}
			catch (Exception ex)
			{
				throw new ArgumentException($"{nameof(ListenPort)} ({listenPort}) is already in use.", ex);
			}
		}

		#endregion Constructors

		#region Properties

		/// <summary>
		/// Gets the Modbus client used to request the remote device, that should be proxied.
		/// </summary>
		public ModbusClientBase Client { get; }

		/// <summary>
		/// Gets the <see cref="IPAddress"/> to listen on.
		/// </summary>
		public IPAddress ListenAddress { get; }

		/// <summary>
		/// Get the port to listen on.
		/// </summary>
		public int ListenPort { get; }

		/// <summary>
		/// Gets a value indicating whether the server is running.
		/// </summary>
		public bool IsRunning => _listener?.Server.IsBound ?? false;

		/// <summary>
		/// Gets or sets the read/write timeout for the incoming connections (not the <see cref="Client"/>!).
		/// </summary>
		public TimeSpan ReadWriteTimeout { get; set; }

		#endregion Properties

		#region Control Methods

		/// <summary>
		/// Starts the server.
		/// </summary>
		/// <param name="cancellationToken">A cancellation token used to propagate notification that this operation should be canceled.</param>
		public Task StartAsync(CancellationToken cancellationToken = default)
		{
			Assertions();

			_stopCts?.Cancel();

			_listener?.Stop();
#if NET8_0_OR_GREATER
			_listener?.Dispose();
#endif

			_stopCts?.Dispose();
			_stopCts = new CancellationTokenSource();

			_listener = new TcpListener(ListenAddress, ListenPort);
			if (ListenAddress.AddressFamily == AddressFamily.InterNetworkV6)
				_listener.Server.DualMode = true;

			_listener.Start();
			_clientConnectTask = WaitForClientAsync(_stopCts.Token);

			return Task.CompletedTask;
		}

		/// <summary>
		/// Stops the server.
		/// </summary>
		/// <param name="cancellationToken">A cancellation token used to propagate notification that this operation should be canceled.</param>
		public Task StopAsync(CancellationToken cancellationToken = default)
		{
			Assertions();
			return StopAsyncInternal(cancellationToken);
		}

		private async Task StopAsyncInternal(CancellationToken cancellationToken = default)
		{
			_stopCts.Cancel();

			_listener.Stop();
#if NET8_0_OR_GREATER
			_listener.Dispose();
#endif
			try
			{
				await Task.WhenAny(_clientConnectTask, Task.Delay(Timeout.Infinite, cancellationToken));
			}
			catch (OperationCanceledException)
			{
				// Terminated
			}

			try
			{
				await Task.WhenAny(Task.WhenAll(_clientTasks), Task.Delay(Timeout.Infinite, cancellationToken));
			}
			catch (OperationCanceledException)
			{
				// Terminated
			}
		}

		/// <summary>
		/// Releases all managed and unmanaged resources used by the <see cref="ModbusTcpProxy"/>.
		/// </summary>
		public void Dispose()
		{
			if (_isDisposed)
				return;

			_isDisposed = true;

			StopAsyncInternal(CancellationToken.None).Wait();

			_clientListLock.Dispose();
			_clients.Clear();

			_stopCts?.Dispose();
		}

		private void Assertions()
		{
#if NET8_0_OR_GREATER
			ObjectDisposedException.ThrowIf(_isDisposed, this);
#else
			if (_isDisposed)
				throw new ObjectDisposedException(GetType().FullName);
#endif
		}

		#endregion Control Methods

		#region Client Handling

		private async Task WaitForClientAsync(CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
#if NET8_0_OR_GREATER
					var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
#else
					var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
#endif
					await _clientListLock.WaitAsync(cancellationToken).ConfigureAwait(false);
					try
					{
						_clients.Add(client);
						_clientTasks.Add(HandleClientAsync(client, cancellationToken));
					}
					finally
					{
						_clientListLock.Release();
					}
				}
				catch
				{
					// There might be a failure here, that's ok, just keep it quiet
				}
			}
		}

		private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
		{
			try
			{
				var stream = client.GetStream();
				while (!cancellationToken.IsCancellationRequested)
				{
					var requestBytes = new List<byte>();

					using (var cts = new CancellationTokenSource(ReadWriteTimeout))
					using (cancellationToken.Register(cts.Cancel))
					{
						byte[] headerBytes = await stream.ReadExpectedBytesAsync(6, cts.Token).ConfigureAwait(false);
						requestBytes.AddRange(headerBytes);

						byte[] followingCountBytes = headerBytes.Skip(4).Take(2).ToArray();
						followingCountBytes.SwapBigEndian();
						int followingCount = BitConverter.ToUInt16(followingCountBytes, 0);

						byte[] bodyBytes = await stream.ReadExpectedBytesAsync(followingCount, cts.Token).ConfigureAwait(false);
						requestBytes.AddRange(bodyBytes);
					}

					byte[] responseBytes = await HandleRequestAsync([.. requestBytes], cancellationToken).ConfigureAwait(false);
					if (responseBytes != null)
						await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
				}
			}
			catch
			{
				// Keep client processing quiet
			}
			finally
			{
				await _clientListLock.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					_clients.Remove(client);
					client.Dispose();
				}
				finally
				{
					_clientListLock.Release();
				}
			}
		}

		#endregion Client Handling

		#region Request Handling

		private Task<byte[]> HandleRequestAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			switch ((ModbusFunctionCode)requestBytes[7])
			{
				case ModbusFunctionCode.ReadCoils:
					return HandleReadCoilsAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.ReadDiscreteInputs:
					return HandleReadDiscreteInputsAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.ReadHoldingRegisters:
					return HandleReadHoldingRegistersAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.ReadInputRegisters:
					return HandleReadInputRegistersAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.WriteSingleCoil:
					return HandleWriteSingleCoilAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.WriteSingleRegister:
					return HandleWriteSingleRegisterAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.WriteMultipleCoils:
					return HandleWriteMultipleCoilsAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.WriteMultipleRegisters:
					return HandleWriteMultipleRegistersAsync(requestBytes, cancellationToken);

				case ModbusFunctionCode.EncapsulatedInterface:
					return HandleEncapsulatedInterfaceAsync(requestBytes, cancellationToken);

				default: // unknown function
					{
						byte[] responseBytes = new byte[9];
						Array.Copy(requestBytes, 0, responseBytes, 0, 8);

						// Mark as error
						responseBytes[7] |= 0x80;

						responseBytes[8] = (byte)ModbusErrorCode.IllegalFunction;
						return Task.FromResult(responseBytes);
					}
			}
		}

		private async Task<byte[]> HandleReadCoilsAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 12)
				return null;

			byte unitId = requestBytes[6];
			ushort firstAddress = requestBytes.GetBigEndianUInt16(8);
			ushort count = requestBytes.GetBigEndianUInt16(10);

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));
			try
			{
				var coils = await Client.ReadCoilsAsync(unitId, firstAddress, count, cancellationToken).ConfigureAwait(false);

				byte[] values = new byte[(int)Math.Ceiling(coils.Count / 8.0)];
				for (int i = 0; i < coils.Count; i++)
				{
					if (coils[i].Value)
					{
						int byteIndex = i / 8;
						int bitIndex = i % 8;

						values[byteIndex] |= (byte)(1 << bitIndex);
					}
				}

				responseBytes.Add((byte)values.Length);
				responseBytes.AddRange(values);
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleReadDiscreteInputsAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 12)
				return null;

			byte unitId = requestBytes[6];
			ushort firstAddress = requestBytes.GetBigEndianUInt16(8);
			ushort count = requestBytes.GetBigEndianUInt16(10);

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));
			try
			{
				var discreteInputs = await Client.ReadDiscreteInputsAsync(unitId, firstAddress, count, cancellationToken).ConfigureAwait(false);

				byte[] values = new byte[(int)Math.Ceiling(discreteInputs.Count / 8.0)];
				for (int i = 0; i < discreteInputs.Count; i++)
				{
					if (discreteInputs[i].Value)
					{
						int byteIndex = i / 8;
						int bitIndex = i % 8;

						values[byteIndex] |= (byte)(1 << bitIndex);
					}
				}

				responseBytes.Add((byte)values.Length);
				responseBytes.AddRange(values);
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleReadHoldingRegistersAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 12)
				return null;

			byte unitId = requestBytes[6];
			ushort firstAddress = requestBytes.GetBigEndianUInt16(8);
			ushort count = requestBytes.GetBigEndianUInt16(10);

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));
			try
			{
				var holdingRegisters = await Client.ReadHoldingRegistersAsync(unitId, firstAddress, count, cancellationToken).ConfigureAwait(false);

				byte[] values = new byte[holdingRegisters.Count * 2];
				for (int i = 0; i < holdingRegisters.Count; i++)
				{
					values[i * 2] = holdingRegisters[i].HighByte;
					values[i * 2 + 1] = holdingRegisters[i].LowByte;
				}

				responseBytes.Add((byte)values.Length);
				responseBytes.AddRange(values);
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleReadInputRegistersAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 12)
				return null;

			byte unitId = requestBytes[6];
			ushort firstAddress = requestBytes.GetBigEndianUInt16(8);
			ushort count = requestBytes.GetBigEndianUInt16(10);

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));
			try
			{
				var inputRegisters = await Client.ReadInputRegistersAsync(unitId, firstAddress, count, cancellationToken).ConfigureAwait(false);

				byte[] values = new byte[count * 2];
				for (int i = 0; i < count; i++)
				{
					values[i * 2] = inputRegisters[i].HighByte;
					values[i * 2 + 1] = inputRegisters[i].LowByte;
				}

				responseBytes.Add((byte)values.Length);
				responseBytes.AddRange(values);
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleWriteSingleCoilAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 12)
				return null;

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));

			ushort address = requestBytes.GetBigEndianUInt16(8);

			if (requestBytes[10] != 0x00 && requestBytes[10] != 0xFF)
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.IllegalDataValue);
				return [.. responseBytes];
			}

			try
			{
				var coil = new Coil
				{
					Address = address,
					HighByte = requestBytes[10],
					LowByte = requestBytes[11],
				};

				bool isSuccess = await Client.WriteSingleCoilAsync(requestBytes[6], coil, cancellationToken).ConfigureAwait(false);
				if (isSuccess)
				{
					// Response is an echo of the request
					responseBytes.AddRange(requestBytes.Skip(8).Take(4));
				}
				else
				{
					responseBytes[7] |= 0x80;
					responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
				}
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleWriteSingleRegisterAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 12)
				return null;

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));

			ushort address = requestBytes.GetBigEndianUInt16(8);

			try
			{
				var register = new HoldingRegister
				{
					Address = address,
					HighByte = requestBytes[10],
					LowByte = requestBytes[11]
				};

				bool isSuccess = await Client.WriteSingleHoldingRegisterAsync(requestBytes[6], register, cancellationToken).ConfigureAwait(false);
				if (isSuccess)
				{
					// Response is an echo of the request
					responseBytes.AddRange(requestBytes.Skip(8).Take(4));
				}
				else
				{
					responseBytes[7] |= 0x80;
					responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
				}
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleWriteMultipleCoilsAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 13)
				return null;

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));

			ushort firstAddress = requestBytes.GetBigEndianUInt16(8);
			ushort count = requestBytes.GetBigEndianUInt16(10);

			int byteCount = (int)Math.Ceiling(count / 8.0);
			if (requestBytes.Length < 13 + byteCount)
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.IllegalDataValue);
				return [.. responseBytes];
			}

			try
			{
				int baseOffset = 13;
				var coils = new List<Coil>();
				for (int i = 0; i < count; i++)
				{
					int bytePosition = i / 8;
					int bitPosition = i % 8;

					ushort address = (ushort)(firstAddress + i);
					bool value = (requestBytes[baseOffset + bytePosition] & (1 << bitPosition)) > 0;

					coils.Add(new Coil
					{
						Address = address,
						HighByte = value ? (byte)0xFF : (byte)0x00
					});
				}

				bool isSuccess = await Client.WriteMultipleCoilsAsync(requestBytes[6], coils, cancellationToken).ConfigureAwait(false);
				if (isSuccess)
				{
					// Response is an echo of the request
					responseBytes.AddRange(requestBytes.Skip(8).Take(4));
				}
				else
				{
					responseBytes[7] |= 0x80;
					responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
				}
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleWriteMultipleRegistersAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			if (requestBytes.Length < 13)
				return null;

			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));

			ushort firstAddress = requestBytes.GetBigEndianUInt16(8);
			ushort count = requestBytes.GetBigEndianUInt16(10);

			int byteCount = count * 2;
			if (requestBytes.Length < 13 + byteCount)
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.IllegalDataValue);
				return [.. responseBytes];
			}

			try
			{
				int baseOffset = 13;
				var list = new List<HoldingRegister>();
				for (int i = 0; i < count; i++)
				{
					ushort address = (ushort)(firstAddress + i);

					list.Add(new HoldingRegister
					{
						Address = address,
						HighByte = requestBytes[baseOffset + i * 2],
						LowByte = requestBytes[baseOffset + i * 2 + 1]
					});

					bool isSuccess = await Client.WriteMultipleHoldingRegistersAsync(requestBytes[6], list, cancellationToken).ConfigureAwait(false);
					if (isSuccess)
					{
						// Response is an echo of the request
						responseBytes.AddRange(requestBytes.Skip(8).Take(4));
					}
					else
					{
						responseBytes[7] |= 0x80;
						responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
					}
				}
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
			}

			return [.. responseBytes];
		}

		private async Task<byte[]> HandleEncapsulatedInterfaceAsync(byte[] requestBytes, CancellationToken cancellationToken)
		{
			var responseBytes = new List<byte>();
			responseBytes.AddRange(requestBytes.Take(8));

			if (requestBytes[8] != 0x0E)
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.IllegalFunction);
				return [.. responseBytes];
			}

			var firstObject = (ModbusDeviceIdentificationObject)requestBytes[10];
			if (0x06 < requestBytes[10] && requestBytes[10] < 0x80)
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.IllegalDataAddress);
				return [.. responseBytes];
			}

			var category = (ModbusDeviceIdentificationCategory)requestBytes[9];
			if (!Enum.IsDefined(typeof(ModbusDeviceIdentificationCategory), category))
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.IllegalDataValue);
				return [.. responseBytes];
			}

			try
			{
				var res = await Client.ReadDeviceIdentificationAsync(requestBytes[6], category, firstObject, cancellationToken).ConfigureAwait(false);

				var bodyBytes = new List<byte>();

				// MEI, Category
				bodyBytes.AddRange(requestBytes.Skip(8).Take(2));

				// Conformity
				bodyBytes.Add((byte)category);
				if (res.IsIndividualAccessAllowed)
					bodyBytes[2] |= 0x80;

				// More, NextId, NumberOfObjects
				bodyBytes.AddRange(new byte[3]);

				int maxObjectId;
				switch (category)
				{
					case ModbusDeviceIdentificationCategory.Basic:
						maxObjectId = 0x02;
						break;

					case ModbusDeviceIdentificationCategory.Regular:
						maxObjectId = 0x06;
						break;

					case ModbusDeviceIdentificationCategory.Extended:
						maxObjectId = 0xFF;
						break;

					default: // Individual
						maxObjectId = requestBytes[10];
						break;
				}

				byte numberOfObjects = 0;
				for (int i = requestBytes[10]; i <= maxObjectId; i++)
				{
					// Reserved
					if (0x07 <= i && i <= 0x7F)
						continue;

					byte[] objBytes = GetDeviceObject((byte)i, res);

					// We need to split the response if it would exceed the max ADU size
					if (responseBytes.Count + bodyBytes.Count + objBytes.Length > TcpProtocol.MAX_ADU_LENGTH)
					{
						bodyBytes[3] = 0xFF;
						bodyBytes[4] = (byte)i;

						bodyBytes[5] = numberOfObjects;
						responseBytes.AddRange(bodyBytes);
						return [.. responseBytes];
					}

					bodyBytes.AddRange(objBytes);
					numberOfObjects++;
				}

				bodyBytes[5] = numberOfObjects;
				responseBytes.AddRange(bodyBytes);
				return [.. responseBytes];
			}
			catch
			{
				responseBytes[7] |= 0x80;
				responseBytes.Add((byte)ModbusErrorCode.SlaveDeviceFailure);
				return [.. responseBytes];
			}
		}

		private byte[] GetDeviceObject(byte objectId, DeviceIdentification deviceIdentification)
		{
			var result = new List<byte> { objectId };
			switch ((ModbusDeviceIdentificationObject)objectId)
			{
				case ModbusDeviceIdentificationObject.VendorName:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.VendorName);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				case ModbusDeviceIdentificationObject.ProductCode:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.ProductCode);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				case ModbusDeviceIdentificationObject.MajorMinorRevision:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.MajorMinorRevision);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				case ModbusDeviceIdentificationObject.VendorUrl:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.VendorUrl);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				case ModbusDeviceIdentificationObject.ProductName:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.ProductName);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				case ModbusDeviceIdentificationObject.ModelName:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.ModelName);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				case ModbusDeviceIdentificationObject.UserApplicationName:
					{
						byte[] bytes = Encoding.UTF8.GetBytes(deviceIdentification.UserApplicationName);
						result.Add((byte)bytes.Length);
						result.AddRange(bytes);
					}
					break;

				default:
					{
						if (deviceIdentification.ExtendedObjects.ContainsKey(objectId))
						{
							byte[] bytes = deviceIdentification.ExtendedObjects[objectId];
							result.Add((byte)bytes.Length);
							result.AddRange(bytes);
						}
						else
						{
							result.Add(0x00);
						}
					}
					break;
			}

			return [.. result];
		}

		#endregion Request Handling
	}
}

﻿
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace HerboldRacing
{
	public class IRSDKSharper
	{
		private const string MapName = "Local\\IRSDKMemMapFileName";
		private const string EventName = "Local\\IRSDKDataValidEvent";
		private const string BroadcastMessageName = "IRSDK_BROADCASTMSG";

		public IRacingSdkData? Data { get; private set; } = null;

		public bool IsStarted { get; private set; } = false;
		public bool IsConnected { get; private set; } = false;

		public event Action<Exception>? OnException = null;
		public event Action? OnConnected = null;
		public event Action? OnDisconnected = null;
		public event Action? OnTelemetryData = null;
		public event Action? OnSessionInfo = null;

		private bool stopNow = false;

		private bool connectionLoopRunning = false;
		private bool telemetryDataLoopRunning = false;
		private bool sessionInfoLoopRunning = false;

		private MemoryMappedFile? memoryMappedFile = null;
		private MemoryMappedViewAccessor? memoryMappedViewAccessor = null;

		private IntPtr? hEvent = null;
		private AutoResetEvent? simulatorAutoResetEvent = null;
		private AutoResetEvent? sessionInfoAutoResetEvent = null;

		private int lastSessionInfoUpdate = -1;
		private int sessionInfoUpdateChangedCount = 0;
		private int sessionInfoUpdateReady = 0;

		private readonly int broadcastWindowMessage = Windows.RegisterWindowMessage( BroadcastMessageName ).ToInt32();

		public void Start()
		{
			Debug.WriteLine( "IRSDKSharper starting..." );

			if ( IsStarted )
			{
				throw new Exception( "IRSDKSharper has already been started." );
			}

			Task.Run( ConnectionLoop );

			IsStarted = true;

			Debug.WriteLine( "IRSDKSharper started." );
		}

		public void Stop()
		{
			Debug.WriteLine( "IRSDKSharper stopping..." );

			if ( !IsStarted )
			{
				throw new Exception( "IRSDKSharper has not been started." );
			}

			Debug.WriteLine( "Setting stopNow = true." );

			stopNow = true;

			if ( sessionInfoLoopRunning )
			{
				Debug.WriteLine( "Waiting for session info loop to stop..." );

				sessionInfoAutoResetEvent?.Set();

				while ( sessionInfoLoopRunning )
				{
					Thread.Sleep( 0 );
				}
			}

			if ( telemetryDataLoopRunning )
			{
				Debug.WriteLine( "Waiting for telemetry data loop to stop..." );

				while ( telemetryDataLoopRunning )
				{
					Thread.Sleep( 0 );
				}
			}

			Data = null;

			if ( connectionLoopRunning )
			{
				Debug.WriteLine( "Waiting for connection loop to stop..." );

				while ( connectionLoopRunning )
				{
					Thread.Sleep( 0 );
				}
			}

			sessionInfoAutoResetEvent = null;
			simulatorAutoResetEvent = null;

			if ( hEvent != null )
			{
				Windows.CloseHandle( (IntPtr) hEvent );

				hEvent = null;
			}

			memoryMappedViewAccessor = null;
			memoryMappedFile = null;

			IsStarted = false;

			Debug.WriteLine( "IRSDKSharper stopped." );
		}

		public void BroadcastMessage( IRacingSdkEnum.BroadcastMsg msg, int var1, int var2, int var3 )
		{
			// TODO handle exceptions - get info when pinvoke site comes back up
			Windows.PostMessage( (IntPtr) 0xFFFF, broadcastWindowMessage, Windows.MakeLong( (short) msg, (short) var1 ), Windows.MakeLong( (short) var2, (short) var3 ) );
		}

		private void ConnectionLoop()
		{
			Debug.WriteLine( "Connection loop started." );

			try
			{
				connectionLoopRunning = true;

				while ( !stopNow )
				{
					if ( memoryMappedFile == null )
					{
						try
						{
							memoryMappedFile = MemoryMappedFile.OpenExisting( MapName );
						}
						catch ( FileNotFoundException )
						{
						}
					}

					if ( memoryMappedFile != null )
					{
						Debug.WriteLine( "memoryMappedFile != null" );

						memoryMappedViewAccessor = memoryMappedFile.CreateViewAccessor();

						hEvent = Windows.OpenEvent( Windows.EVENT_ALL_ACCESS, false, EventName );

						if ( hEvent == null )
						{
							int errorCode = Marshal.GetLastWin32Error();

							Marshal.ThrowExceptionForHR( errorCode, IntPtr.Zero );
						}
						else
						{
							simulatorAutoResetEvent = new AutoResetEvent( false )
							{
								SafeWaitHandle = new SafeWaitHandle( (IntPtr) hEvent, true )
							};

							sessionInfoAutoResetEvent = new AutoResetEvent( false );

							Task.Run( TelemetryDataLoop );
							Task.Run( SessionInfoLoop );
						}

						break;
					}
					else
					{
						Thread.Sleep( 250 );
					}
				}

				connectionLoopRunning = false;
			}
			catch ( Exception exception )
			{
				Debug.WriteLine( "Connection loop exception caught." );

				connectionLoopRunning = false;

				OnException?.Invoke( exception );
			}

			Debug.WriteLine( "Connection loop stopped." );
		}

		private void TelemetryDataLoop()
		{
			Debug.WriteLine( "Telemetry data loop starting." );

			try
			{
				telemetryDataLoopRunning = true;

				while ( !stopNow )
				{
					var signalReceived = simulatorAutoResetEvent?.WaitOne( 250 ) ?? false;

					if ( signalReceived )
					{
						if ( Data == null )
						{
							Debug.WriteLine( "Connected to iRacing simulator." );

							Data = new IRacingSdkData( memoryMappedViewAccessor );

							IsConnected = true;

							lastSessionInfoUpdate = -1;
							sessionInfoUpdateReady = 0;

							OnConnected?.Invoke();
						}

						Data.Update();

						if ( lastSessionInfoUpdate != Data.SessionInfoUpdate )
						{
							Debug.WriteLine( "iRacingSdkData.SessionInfoUpdate changed." );

							lastSessionInfoUpdate = Data.SessionInfoUpdate;

							Interlocked.Increment( ref sessionInfoUpdateChangedCount );

							sessionInfoAutoResetEvent?.Set();
						}

						if ( Interlocked.Exchange( ref sessionInfoUpdateReady, 0 ) == 1 )
						{
							Debug.WriteLine( "Invoking OnSessionInfo..." );

							OnSessionInfo?.Invoke();
						}

						OnTelemetryData?.Invoke();
					}
					else
					{
						if ( Data != null )
						{
							Debug.WriteLine( "Disconnected from iRacing simulator." );

							if ( sessionInfoUpdateChangedCount > 0 )
							{
								Debug.WriteLine( "Draining sessionInfoUpdateChangedCount..." );

								while ( sessionInfoUpdateChangedCount > 0 )
								{
									Thread.Sleep( 0 );
								}
							}

							Data = null;

							IsConnected = false;

							Debug.WriteLine( "Invoking OnDisconnected..." );

							OnDisconnected?.Invoke();
						}
					}
				}

				telemetryDataLoopRunning = false;
			}
			catch ( Exception exception )
			{
				Debug.WriteLine( "Telemetry data loop exception caught." );

				telemetryDataLoopRunning = false;

				OnException?.Invoke( exception );
			}

			Debug.WriteLine( "Telemetry data loop stopped." );
		}

		private void SessionInfoLoop()
		{
			Debug.WriteLine( "Session info loop started." );

			try
			{
				sessionInfoLoopRunning = true;

				while ( !stopNow )
				{
					Debug.WriteLine( "Waiting for session info event." );

					sessionInfoAutoResetEvent?.WaitOne();

					while ( sessionInfoUpdateChangedCount > 0 )
					{
						if ( !stopNow )
						{
							Debug.WriteLine( "Updating session info..." );

							Data?.UpdateSessionInfo();

							sessionInfoUpdateReady = 1;
						}

						Interlocked.Decrement( ref sessionInfoUpdateChangedCount );
					}
				}

				sessionInfoLoopRunning = false;
			}
			catch ( Exception exception )
			{
				Debug.WriteLine( "Session info loop exception caught." );

				sessionInfoLoopRunning = false;

				OnException?.Invoke( exception );
			}

			Debug.WriteLine( "Session info loop stopped." );
		}
	}
}
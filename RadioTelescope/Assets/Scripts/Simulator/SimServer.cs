﻿using System;
using System.IO;
using System.Net; 
using System.Net.Sockets; 
using System.Text; 
using System.Threading; 
using UnityEngine;
using Valve.Newtonsoft.Json;
using Modbus.Data;
using Modbus.Device;
using System.Linq;
using TMPro;
using UnityEngine.UI;
using Valve.VR.InteractionSystem;
using static MCUCommand;

public class SimServer : MonoBehaviour {

	/// <summary>
	/// Private constants
	/// </summary>
	private const int AZIMUTH_GEARING_RATIO = 500;
    private const int ELEVATION_GEARING_RATIO = 50;
	private static int ENCODER_COUNTS_PER_REVOLUTION_BEFORE_GEARING = 8000;
	private const int STEPS_PER_REVOLUTION = 20000;

	/// <summary> 	
	/// TCPListener to listen for incomming TCP connection 	
	/// requests. 	
	/// </summary> 	
	private TcpListener tcpListener; 
	/// <summary> 
	/// Background thread for TcpServer workload. 	
	/// </summary> 	
	private Thread tcpListenerThread;  	
	/// <summary> 	
	/// Create handle to connected tcp client. 	
	/// </summary> 	
	private TcpClient connectedTcpClient;
	private TcpListener MCU_TCPListener;
	private ModbusSlave MCU_Modbusserver;
	private Thread MCU_emulator_thread;
	
	// for controlling the VR telescope
	public TelescopeControllerSim tc;

	public MCUCommand currentCommand;
	public float speed = 0.01f;
	private float azDeg = -42069;
	private float elDeg = -42069;
	
	//UI Related variables
	public TMP_InputField mcuIP;
	public TMP_InputField mcuPort;
	public Button startButton;
	public Button fillButton;
	
	private bool runSimulator = false;
	private bool moving = false;
	private bool jogging = false;
	private bool homing = false;
	private bool isConfigured = false;
	private bool isTest = false;
	private bool isJogComand = false;
	
	/// <summary>
	/// Start is called before the first frame
	/// </summary>
	public void Start()
	{
		//startButton = GetComponent<Button>();
		startButton.onClick.AddListener(StartServer);
		fillButton.onClick.AddListener(AutoFillInput);
		
		//fix the fullscreen stuff
		Screen.fullScreen = false;
		Screen.SetResolution(1024, 768, FullScreenMode.Windowed);

		// create a base current command object
		ushort[] noCommand = {0x0420};
		currentCommand = new MCUCommand(noCommand);
	}
	
	/// <summary>
	///	method tied to fill button which puts in correct values for the sim MCU
	/// </summary>
	public void AutoFillInput()
	{
		mcuIP.text = "127.0.0.1";
		mcuPort.text = "8083";
	}
	
	/// <summary>
	/// Start the MCU server and thread
	/// </summary>
	public void StartServer()
	{
		// Don't start the sim twice and maybe somehow screw something up.
		if(runSimulator)
			return;
		Debug.Log("Start Button clicked");
		
		tc.speed = speed;
		
		try
		{
			MCU_TCPListener = new TcpListener(new IPEndPoint(IPAddress.Parse(mcuIP.text), int.Parse(mcuPort.text)));
			MCU_emulator_thread = new Thread(new ThreadStart(runMCUThread));
		}
		catch (Exception e)
		{
			if((e is ArgumentNullException) || (e is ArgumentOutOfRangeException))
			{
				Debug.Log(e);
				return;
			}
			else
			{
				throw e;
			}
		}
		
		try
		{
			MCU_TCPListener.Start(1);
		}
		catch (Exception e)
		{
			if((e is SocketException) || (e is ArgumentOutOfRangeException) || (e is InvalidOperationException))
			{
				Debug.Log(e);
				return;
			}
		}
		runSimulator = true;
		MCU_emulator_thread.Start();
		startButton.GetComponent<Image>().color = Color.green;
	}
	
	/// <summary>
	/// Update is called once per frame
	/// </summary>
	void Update () 
	{ 		
		// send current command to controller
		if (!currentCommand.errorFlag)
			tc.SetNewMCUCommand(currentCommand);

		// press escape to exit the program cleanly
		if(Input.GetKeyDown((KeyCode.Escape)))
			Application.Quit();
	}
	
	/// <summary>
	/// main "spin" loop for the sim'd MCU server. Handles reading from the datastore and writing back progress of the movement
	/// TODO: what should we be writing back? Are we doing that now? How often should we be writing back?
	/// </summary>
	private void runMCUThread()
	{
		byte slaveId = 1;
		// create and start the modbus server TCP slave
		MCU_Modbusserver = ModbusTcpSlave.CreateTcp(slaveId, MCU_TCPListener);
		// coils, inputs, holdingRegisters, inputRegisters
		MCU_Modbusserver.DataStore = DataStoreFactory.CreateDefaultDataStore(0, 0, 1054, 0);
		
		MCU_Modbusserver.Listen();
		
		ushort[] last, current;
		last = copyModbusDataStoreRegisters(1025, 20);
		while (runSimulator)
		{
			// keep our CPU's alive
			Thread.Sleep(100);
			current = copyModbusDataStoreRegisters(1025, 20);

			// jog commands frequently send the same exact register contents (is jog), so we need a special case for them
			// 0x0080 and 0x0100 tell us the direction of the jog. This is handled in buildMCUCommand.
			// these checks are basically if (are we trying to jog something)
			// 								then constantly check for new register data;
			if (current[(int) RegPos.firstWordAzimuth] == 80 || current[(int) RegPos.firstWordAzimuth] == 100 
					|| current[(int) RegPos.firstWordElevation] == 80 || current[(int) RegPos.firstWordElevation] == 100)
			{
				isJogComand = true;

			} else { isJogComand = false; }

			if(!current.SequenceEqual(last) || isJogComand)
			{
				Debug.Log("--------------------------------------------- !! New Register Data Incoming !!");
				currentCommand = buildMCUCommand(current);
			}
			if(moving)
			{
				// we are still in motion
				// TODO: here we can write back more checks (like if an error happens)
				if(currentCommand.azimuthDegrees != tc.simTelescopeAzimuthDegrees || currentCommand.elevationDegrees != tc.simTelescopeElevationDegrees)
				{
					Debug.Log("SIMSERVER: Move not yet completed");
					updateMCURegistersStillMoving();
					updateMCUPosition();
				}
				else
				{
					Debug.Log("SIMSERVER: MOVE COMPLETED");
					moving = false;
					
					updateMCURegistersFinishedMove();
					updateMCUPosition();
				}
			}
			else if(jogging)
			{
				updateMCURegistersStillMoving();
				updateMCUPosition();
			} else if (homing)
			{
				// we need to catch homing so we can write a proper finished move so the CR knows homing was successful
				if (tc.simTelescopeAzimuthDegrees == 0.0f && tc.simTelescopeElevationDegrees == 0.0f)
				{
					Debug.Log("SIMSERVER: HOMING COMPLETED");
					updateMCURegistersFinishedMove();
					updateMCUPosition();
					homing = false;
				} else 
				{
					Debug.Log("SIMSERVER: Homing not yet complete");
					updateMCURegistersStillMoving();
					updateMCUPosition();
				}
			} else 
			{
				// update position every possible chance
				updateMCUPosition();
			}
			last = current;
		}
	}
	
	private MCUCommand buildMCUCommand(ushort[] data)
	{
		isConfigured = true;
		string outstr = "";
		for(int v = 0; v < data.Length; v++) {
			outstr += Convert.ToString( data[v] , 16 ).PadLeft( 5 ) + ",";
		}
		Debug.Log("Spitting out registers: \n");
		Debug.Log(outstr);
		Debug.Log("All done spitting out registers\n");

		jogging = false;

		// figure out which move we are doing to decide what we write to the input register store (MCU's RESPONSE to CONTROL ROOM)

		if ((data[0] == 0x0080 )|| data[0] == 0x0100 || data[10] == 0x0080 || data[10] == 0x0100) // jog pos and neg, for az and el (az = 0, el = 10)
		{
			jogging = true;

		} else if(data[0] == 0x0002) // RELATIVE MOVE
		{
			moving = true;
		} else if (data[0] == 0x0040) // HOMING
		{
			homing = true;
		}

		// build mcu command based on register data
		currentCommand = new MCUCommand(data, tc.simTelescopeAzimuthDegrees, tc.simTelescopeElevationDegrees);

		updateMCURegistersStillMoving();
		updateMCUPosition();
		return currentCommand;
	}
	
	/// <summary>
	/// Writes to shared register store with the current position of the sim telescope
	/// This needs to convert the degrees of our azimuth and elevation to steps and encoder steps
	/// the CR looks for registers  [2 + 3 = azSteps]
	/// 						    [3 + 4 = azEncoder]
	/// 							[12 + 13 = elSteps]
	/// 							[14 + 15 = elEncoder]
	/// </summary>
	private void updateMCUPosition()
	{
		int azEncoder = degreesToSteps_Encoder(tc.simTelescopeAzimuthDegrees, AZIMUTH_GEARING_RATIO);
		int elEncoder = (-1) * degreesToSteps_Encoder(tc.simTelescopeElevationDegrees, ELEVATION_GEARING_RATIO);
		int azSteps = degreesToSteps(tc.simTelescopeAzimuthDegrees, AZIMUTH_GEARING_RATIO);
		int elSteps = (-1) * degreesToSteps(tc.simTelescopeElevationDegrees, ELEVATION_GEARING_RATIO);

		// write actual values using some magic bit work
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.firstWordAzimuthSteps] = (ushort)((azSteps & 0xffff0000) >> 16);
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.secondWordAzimuthSteps] = (ushort)(azSteps & 0xffff);
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.firstWordElevationSteps] = (ushort)((elSteps & 0xffff0000) >> 16);
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.secondWordElevationSteps] = (ushort)(elSteps & 0xffff);

		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.firstWordAzimuthEncoder] = (ushort)(((int)(azEncoder) & 0xffff0000) >> 16);
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.secondWordAzimuthEncoder] = (ushort)((int)(azEncoder) & 0xffff);
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.firstWordElevationEncoder] = (ushort)(((int)(elEncoder) & 0xffff0000) >> 16);
		MCU_Modbusserver.DataStore.HoldingRegisters[(int) WriteBackRegPos.secondWordElevationEncoder] = (ushort)((int)(elEncoder) & 0xffff);
	}

	/// <summary>
	/// For now we will finish both axes at the same time - in the future this could be split out into seperate calls
	/// the control room looks at again the MSW (bit 0 for AZ, bit 10 for EL) and shifts it with the move complete constant (7 bits to the right), then & with 0b1
	/// </summary>
	private void updateMCURegistersFinishedMove()
	{
		// Azimuth
		MCU_Modbusserver.DataStore.HoldingRegisters[1] = (ushort) MCUWriteBack.finishedMove;
		
		// Elevation
		MCU_Modbusserver.DataStore.HoldingRegisters[11] = (ushort) MCUWriteBack.finishedMove;
	}

	/// <summary>
	/// For now we will update both axes (axis plural, i googled it)
	/// the control room looks for the most significant bit (AZ or EL) and then shifts it with the CCW_Motion constant (1) 
	/// or the CW_Motion constant (0). To show that this is still moving. The 0's are for the shift 1 right 
	/// </summary>
	private void updateMCURegistersStillMoving() 
	{
		// Azimuth
		MCU_Modbusserver.DataStore.HoldingRegisters[1] =  (ushort) MCUWriteBack.stillMoving; 

		// Elevation
		MCU_Modbusserver.DataStore.HoldingRegisters[11] = (ushort) MCUWriteBack.stillMoving;
	}
	
	/// <summary>
	/// returns a copy of the current MCU server datastore (REGISTERS)
	/// </summary>
	private ushort[] copyModbusDataStoreRegisters(int start_index, int length)	{
		ushort[] data = new ushort[length];
		for(int i = 0; i < length; i++)
		{
			data[i] = MCU_Modbusserver.DataStore.HoldingRegisters[i + start_index];
		}
		return data;
	}
	
	/// <summary>
	/// Helper method to convert degrees to the encoder values expected by the control room
	/// </summary>
	/// <param name="degrees"> the actual degrees of the sim telescope (per axis) </param>
	/// <param name="gearingRatio"> corresponds to the axis we are converting </param>
	/// <returns></returns>
	private int degreesToSteps_Encoder(float degrees, int gearingRatio)
	{
		return (int)(degrees * ENCODER_COUNTS_PER_REVOLUTION_BEFORE_GEARING * gearingRatio / 360.0);
	}

	/// <summary>
	/// Helper method to convert degrees back to steps
	/// </summary>
	/// <param name="degrees"> actual degrees of the sim telescope (per axis) </param>
	/// <param name="gearingRatio">  </param>
	/// <returns></returns>
	private int degreesToSteps(float degrees, int gearingRatio)
	{
		return (int)(degrees * STEPS_PER_REVOLUTION * gearingRatio / 360.0);
	}
}

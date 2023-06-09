using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TBS.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameClient : IDisposable
{
    #region Prop's
    private TcpClient client;
    private NetworkStream stream;
    private byte[] buffer = new byte[1024];
    string ipAddress;
    int port;

    public void SetIpAddress(string ip) => ipAddress = ip;
    public void SetPort(int Port) => port = Port;

    public event Action<byte> OnGameStart;
    public event Action<byte> OnSwitchTurns;
    Thread receivemessagethread;
    public bool IsOwner = false;
    public static GameClient Instance { get; private set; }
    int max_recv_time_out = 0;
    #endregion

    #region Game Prop's 

    public event Action<int> OnSpawnUnitsOnSide;
    public event Action<byte> OnSpawnLevelGrid;
    public event Action<string> OnUnitDoAction;

    public event Action<bool> OnPlayerStartGameMove;
    public event Action<byte> OnClientDisconnected;
    #endregion

    #region encryption detals
    EncryptionKeys encryptionKeys;

    string server_public_key;
    #endregion

    #region Client Start And Stop

    public GameClient()
    {
        if (Instance == null && Instance != this)
        {
            Instance = this;
        }
        else
        {
            return;
        }
    }
    public void Connect()
    {  
        try
        {
            encryptionKeys = new EncryptionKeys();
            client = new TcpClient(ipAddress, port);
            max_recv_time_out = client.ReceiveTimeout;
            client.ReceiveTimeout = 1000;
            stream = client.GetStream();

            ExchangePublicKeys();

            receivemessagethread = new Thread(new ThreadStart(ReceiveMessage));
            receivemessagethread.Start();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Debug.Log("Client Connects to server");
        }
        catch (Exception e)
        {
            Debug.Log("Error connecting to server: " + e.Message);
            client = null;
            stream = null;  
            receivemessagethread?.Abort();
            receivemessagethread=null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            return;
        }
    }


    private void ExchangePublicKeys()
    {
        //Recv Server Public Key
       int byteread= stream.Read(buffer, 0, buffer.Length);
        server_public_key = Encoding.UTF8.GetString(buffer,0, byteread);
        Debug.Log("server Public Key: "+server_public_key);
        //Send Client Public Key
        stream.Write(Encoding.UTF8.GetBytes(encryptionKeys.public_key), 0, Encoding.UTF8.GetBytes(encryptionKeys.public_key).Length);
    }

    public void Dispose()
    {
        Stop();
    }
  
    public bool IsConnected()
    {
        return client.Connected;
    }
    public bool HasClinet()
    {
        if (client != null)
            if(client.Connected)           
                return true;           
        return false;
    }

    public void Stop()
    {
        Disconnect();
    }

    public void Disconnect()
    {

        try
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            BaseAction.OnAnyActionStarted -= Action_ActionStarted;
            TurnSystem.Instance.OnTurnChanged -= OnTurnChanged;
            OnClientDisconnected?.Invoke(byte.MinValue);
            if (client.Connected)
                SendMessage("Log Out");
            client.Close();
            stream.Close();
            client.Dispose();
            stream.Dispose();
            Debug.Log("Disconnected from server.");
            receivemessagethread.Abort();
            GC.SuppressFinalize(receivemessagethread);
            GC.SuppressFinalize(this);
            GC.Collect();
        }
        catch (Exception e)
        {
            Debug.Log("Error disconnecting from server: " + e.Message);
            GC.SuppressFinalize(this);
            GC.Collect();
        }
        Instance = null;
        client = null;
        stream = null;  
        receivemessagethread = null;
    }
    #endregion

    #region Client Only

    public void SendMessage(string message)
    {     
        if (client.Connected)
        {
            try
            {
                // Get NetworkStream if TcpClient is connected
                NetworkStream stream = client.GetStream();
                if (stream != null && stream.CanWrite)
                {
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    data = EncryptionHelper.Encrypt(message, server_public_key);
                    Debug.Log("Client Encrypt Message: "+Encoding.UTF8.GetString(data));
                    // Write data to NetworkStream
                    stream.Write(data, 0, data.Length);
                }
            }
            catch (ObjectDisposedException ex)
            {
                // Handle exception, e.g. log or display error message
                Debug.Log("Error sending data: " + ex.Message);
            }
        }
        else
        {
            // Handle error, e.g. client is null or not connected
            Debug.Log("Error sending data: client is not connected");
            if(!client.Connected)
            {
                Disconnect();
            }
        }
    }
    public void ReceiveMessage()
    {
        client.ReceiveTimeout=max_recv_time_out;
        try
        {         
            int bytesRead;
            string response;
            while (client.Connected)
            {
                // read data from the server into the buffer
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                byte[] encryptedData = new byte[bytesRead];
                Array.Copy(buffer, 0, encryptedData, 0, bytesRead);
                response = EncryptionHelper.Decrypt(encryptedData, bytesRead, encryptionKeys.private_key);
                if (response == "")
                    continue;
                Debug.Log(response);
                DoAsTheServerCommends(response);
            }
        }
        catch (Exception e)
        {
            Debug.Log("Error receiving message: " + e.Message);   
            if (client.Connected)
                return;
            Disconnect();
            GC.SuppressFinalize(this);
        }
    }

    #endregion

    #region Game contect

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        SendMessage("Scene loaded: " + scene.name);
        ListenToGameEvents();
        AudioManger.Instance.PlayMusic("GameMainMelody");
    }

    private void ListenToGameEvents()
    {
        //send to to other and cancel the abilty to play
        BaseAction.OnAnyActionStarted += Action_ActionStarted;
        TurnSystem.Instance.OnTurnChanged += OnTurnChanged;
    }


    private void OnTurnChanged(object sender, EventArgs e)
    {
        if (TurnSystem.Instance.IsPlayerTurn()) { return; }
        SendMessage("Switch Turns");
    }

    private void Action_ActionStarted(object sender, EventArgs e)
    {
        if (UnitManager.Instance.GetEnemyUnitList().Contains(((BaseAction)sender).GetUnit()))
            return;
        SendMessage(((BaseAction)sender).GetActionAsString());
    }

    private void DoAsTheServerCommends(string servercommend)
    {
        if (StartBoardCommands(servercommend)) { return; }
        if (GameActionsCommands(servercommend)) { return; }
        if (GameSwitchTurnsCommands(servercommend)) { return; }
    }

    private bool GameSwitchTurnsCommands(string servercommend)
    {
        if (!servercommend.Contains("Switch"))
            return false;
        MainThreadDispatcher.ExecuteOnMainThread(OnSwitchTurns, byte.MinValue);
        return true;
    }

    private bool GameActionsCommands(string servercommend)
    {
        if (!servercommend.Contains("Action"))
            return false;
        MainThreadDispatcher.ExecuteOnMainThread(OnUnitDoAction, servercommend);
        return true;

    }

    private bool StartBoardCommands(string servercommend)
    {
        if (servercommend.Contains("Game Has Been Started"))
        {
            MainThreadDispatcher.ExecuteOnMainThread(OnGameStart, byte.MinValue);
            return true;
        }
        else if (servercommend.Contains("SetBoard"))
        {
            MainThreadDispatcher.ExecuteOnMainThread(OnSpawnLevelGrid, byte.MinValue);
            return true;
        }
        else if (servercommend.Contains("Instantiate Units"))
        {
            if (servercommend.Contains("Side One"))
            {
                MainThreadDispatcher.ExecuteOnMainThread(OnSpawnUnitsOnSide, 1);
            }
            else
            {
                MainThreadDispatcher.ExecuteOnMainThread(OnSpawnUnitsOnSide, 2);
            }
            return true;
        }
        else if (servercommend.Contains("First Move"))
        {
            MainThreadDispatcher.ExecuteOnMainThread(OnPlayerStartGameMove, true);
            return true;
        }
        else if (servercommend.Contains("Not You'r Move"))
        {
            MainThreadDispatcher.ExecuteOnMainThread(OnPlayerStartGameMove, false);
            return true;
        }
        return false;
    }

    

    #endregion

}
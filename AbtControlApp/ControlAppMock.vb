Imports System.IO
Imports System.IO.Pipes
Imports System.Text
Imports System.Threading

''' <summary>
''' 制御アプリ模擬（ABTコントロールとの名前付きパイプ通信テスト用）
''' ・dt_ABTcontrolIS にクライアントとして接続（コマンド送信）
''' ・dt_ABTcontrolS をサーバとして待ち受け（Result/Event 受信）
''' </summary>
Public Class ControlAppMock

    ' ★ ABT 側の実装に合わせたパイプ名
    Private Const CommandPipeName As String = "dt_ABTcontrolIS"   ' 制御→ABT (ABT側 NamedPipeReceiver のサーバ)
    Private Const ResultPipeName As String = "dt_ABTcontrolS"     ' ABT→制御 (ABT側 NamedPipeSender のクライアント)

    ' ★ エンコーディング
    Private ReadOnly commandEncoding As Encoding = Encoding.GetEncoding(932) ' 制御→ABT（Receiver側が UTF8をSJISに修正）
    Private ReadOnly resultEncoding As Encoding = Encoding.GetEncoding(932) ' ABT→制御（Sender側が SJIS）

    ' パイプ実体
    Private commandClient As NamedPipeClientStream
    Private resultServer As NamedPipeServerStream
    Private resultReader As StreamReader

    Private recvThread As Thread
    Private recvCts As CancellationTokenSource

    '判定要求コマンド定義
    ' 132文字のQRコード (args(1))
    'Dim qrCode132 As String = New String("A"c, 132)
    Dim qrCode132 As String = New String("1"c, 132)

    ' 24文字のQRチケット番号 (args(2))
    ' Dim qrTicket24 As String = New String("B"c, 24)
    Dim qrTicket24 As String = New String("2"c, 24)

    ' 要求日時 (yyyyMMddHHmmssff) (args(3))
    Dim reqTime16 As String = "2025120615150000" 

    ' 処理方向 (args(0))
    Dim procDir As String = "01" 

    ' 各フラグと駅情報コード (args(4) ～ args(13))
    ' 00/01/02 などの許容値と2桁長をクリアする値
    Dim issueDisFlag As String = "00"    ' 発行障害フラグ
    Dim appBailFlag As String = "00"     ' 出場救済フラグ
    Dim offlineTktFlag As String = "01"  ' オフライン改札機利用フラグ
    Dim execPermitFlag As String = "00"  ' 実行許可フラグ
    Dim modelType As String = "02"       ' 媒体種別 (01～04)
    Dim otherStaAppFlag As String = "01" ' 他駅入出場フラグ
    Dim bizOpRegCode As String = "10"    ' 地域コード
    Dim bizOpUserCode As String = "20"   ' ユーザコード
    Dim lineSec As String = "30"         ' 線区
    Dim staOrder As String = "40"        ' 駅順



    ' ★ 外から受信メッセージを見られるようにイベントにする（ログでもOK）
    Public Event ReceivedLine(line As String)

    '==============================
    ' 接続開始
    '==============================
    Public Sub Start()
        ' 1. ABT→制御用のサーバ(dt_ABTcontrolS)を先に立てる
        resultServer = New NamedPipeServerStream(
            ResultPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        )
        Console.WriteLine($"[Mock] Waiting for ABT to connect on {ResultPipeName} ...")
        resultServer.WaitForConnection()
        Console.WriteLine($"[Mock] ABT connected to {ResultPipeName}.")
        resultReader = New StreamReader(resultServer, resultEncoding)

        ' 2. 制御→ABT 用のクライアント(dt_ABTcontrolIS)を接続
        commandClient = New NamedPipeClientStream(
            ".",
            CommandPipeName,
            PipeDirection.Out
        )
        Console.WriteLine($"[Mock] Connecting to ABT on {CommandPipeName} ...")
        commandClient.Connect()
        Console.WriteLine($"[Mock] Connected to {CommandPipeName}.")

        ' 3. 受信ループを別スレッドで開始（Result/Event を非同期に読む）
        recvCts = New CancellationTokenSource()
        recvThread = New Thread(AddressOf ReceiveLoop) With {.IsBackground = True}
        recvThread.Start()
    End Sub

    '==============================
    ' 終了処理
    '==============================
    Public Sub [Stop]()
        Try
            If recvCts IsNot Nothing Then
                recvCts.Cancel()
            End If
        Catch
        End Try

        Try
            If resultReader IsNot Nothing Then
                resultReader.Dispose()
                resultReader = Nothing
            End If
        Catch
        End Try

        Try
            If resultServer IsNot Nothing Then
                resultServer.Dispose()
                resultServer = Nothing
            End If
        Catch
        End Try

        Try
            If commandClient IsNot Nothing Then
                commandClient.Dispose()
                commandClient = Nothing
            End If
        Catch
        End Try

        Console.WriteLine("[Mock] Stopped.")
    End Sub

    '==============================
    ' 制御→ABT 送信（1行）
    '==============================
    Public Sub SendLine(text As String)
        If commandClient Is Nothing OrElse Not commandClient.IsConnected Then
            Throw New InvalidOperationException("ABTコントロールに接続されていません。Start() 済みか確認してください。")
        End If

        Dim line As String = text & vbCrLf
        Dim bytes = commandEncoding.GetBytes(line)
        commandClient.Write(bytes, 0, bytes.Length)
        commandClient.Flush()

        Console.WriteLine("[SEND] " & text)
    End Sub

    '==============================
    ' ABT→制御 受信ループ
    '==============================
    Private Sub ReceiveLoop()
        Try
            While Not recvCts.IsCancellationRequested
                Dim line As String = resultReader.ReadLine()
                If line Is Nothing Then
                    Exit While ' 切断
                End If

                RaiseEvent ReceivedLine(line)
                Console.WriteLine("[RECV] " & line)
            End While
        Catch ex As Exception
            Console.WriteLine("[Mock] ReceiveLoop error: " & ex.Message)
        End Try

        Console.WriteLine("[Mock] ReceiveLoop end.")
    End Sub

    '==============================
    ' IT-01 シナリオ実行ヘルパ
    '==============================
    ''' <summary>
    ''' IT-01「起動時（タンキングデータ無し）」シナリオを一気に流すための補助メソッド。
    ''' 手動で送受信を確認したいだけなら、Main側から SendLine を好きに呼んでもOK。
    ''' </summary>
    Public Sub RunIt01Scenario()
        ' 1) 起動処理コマンド
        SendLine("Call,AbtOpen,0")

        ' 2) 認証データ要求コマンド（試験用固定値）
        '    駅務機器情報：00112233445566778899（例）
        '    認証データ送信日時：20250701（例）
        SendLine("Call,AbtAuthenticationData,00112233445566778899,20250701")

        ' あとは ReceiveLoop 側で Result / Event を受信してログに出る。
        ' 必要であれば、Main から Console.ReadKey() 等でしばらく待機する。
    End Sub

    ' 判定要求実行
    Public Sub TestRequestJudgment()
        Dim commandLine As String = _
            $"Call,AbtTicketGateJudgment," & _
            $"{procDir}," & _          ' 引数 1: 処理方向
            $"{qrCode132}," & _        ' 引数 2: QRコード (132桁)
            $"{qrTicket24}," & _       ' 引数 3: QRチケット番号 (24桁)
            $"{reqTime16}," & _        ' 引数 4: 要求日時 (16桁)
            $"{issueDisFlag}," & _     ' 引数 5: 発行障害フラグ
            $"{appBailFlag}," & _      ' 引数 6: 出場救済フラグ
            $"{offlineTktFlag}," & _   ' 引数 7: オフライン改札機利用フラグ
            $"{execPermitFlag}," & _    ' 引数 8: 実行許可フラグ
            $"{modelType}," & _         ' 引数 9: 媒体種別
            $"{otherStaAppFlag}," & _   ' 引数 10: 他駅入出場フラグ
            $"{bizOpRegCode}," & _      ' 引数 11: 地域コード
            $"{bizOpUserCode}," & _     ' 引数 12: ユーザコード
            $"{lineSec}," & _           ' 引数 13: 線区
            $"{staOrder}"               ' 引数 14: 駅順

        SendLine(commandLine)
    End Sub
End Class

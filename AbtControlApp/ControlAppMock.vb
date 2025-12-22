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
    Private ReadOnly commandEncoding As Encoding = Encoding.GetEncoding(932) ' 制御→ABT（Receiver側が SJIS）
    Private ReadOnly resultEncoding As Encoding = Encoding.GetEncoding(932)  ' ABT→制御（Sender側が SJIS）

    ' パイプ実体
    Private commandClient As NamedPipeClientStream
    Private resultServer As NamedPipeServerStream
    Private resultReader As StreamReader

    Private recvThread As Thread
    Private recvCts As CancellationTokenSource

        ' 終了レスポンス待ち用
    Private closeResultLine As String = Nothing
    Private closeResultEvent As New AutoResetEvent(False)

    '==============================
    ' 判定要求コマンド定義
    '==============================

    ' 132文字のQRコード (args(1))
    Private qrCode132 As String = New String("1"c, 132)

    ' 24文字のQRチケット番号 (args(2))
    Private qrTicket24 As String = New String("2"c, 24)

    ' 要求日時 (yyyyMMddHHmmssff) (args(3))
    Private reqTime16 As String = "2025120615150000"

    ' 処理方向 (args(0))
    Private procDir As String = "01"

    ' 各フラグと駅情報コード (args(4) ～ args(13))
    Private issueDisFlag As String = "00"    ' 発行障害フラグ
    Private appBailFlag As String = "00"     ' 出場救済フラグ
    Private offlineTktFlag As String = "01"  ' オフライン改札機利用フラグ
    Private execPermitFlag As String = "00"  ' 実行許可フラグ（TestRequestJudgment 用デフォルト）
    Private modelType As String = "02"       ' 媒体種別 (01～04)
    Private otherStaAppFlag As String = "01" ' 他駅入出場フラグ
    Private bizOpRegCode As String = "10"    ' 地域コード
    Private bizOpUserCode As String = "20"   ' ユーザコード
    Private lineSec As String = "30"         ' 線区
    Private staOrder As String = "40"        ' 駅順

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

            ' ★ 終了レスポンス専用のフック
            If line.StartsWith("Result,AbtClose", StringComparison.OrdinalIgnoreCase) Then
                closeResultLine = line
                closeResultEvent.Set()
            End If

            RaiseEvent ReceivedLine(line)
            Console.WriteLine("[RECV] " & line)
        End While
    Catch ex As Exception
        Console.WriteLine("[Mock] ReceiveLoop error: " & ex.Message)
    End Try

    Console.WriteLine("[Mock] ReceiveLoop end.")
End Sub

     ' ==============================
    ' 終了処理シナリオ実行
    ' ==============================
    ''' <summary>
    ''' Call,AbtClose,0 を送信し、Result,AbtClose を待つ簡易シナリオ。
    ''' </summary>
    Public Sub RunCloseScenario()
  Console.WriteLine("=== 終了処理シナリオ開始 ===")

    ' 前の結果をクリア
    closeResultLine = Nothing

    ' 1) 終了処理コマンド送信（引数1 は予備で 0 固定）
    SendLine("Call,AbtClose,0")

    ' 2) Result,AbtClose を待つ（例：3 秒タイムアウト）
    If closeResultEvent.WaitOne(TimeSpan.FromSeconds(3)) Then
        Console.WriteLine($"[CHECK] 終了レスポンス受信: {closeResultLine}")
    Else
        Console.WriteLine("[CHECK] 3秒以内に Result,AbtClose が返ってきませんでした。")
    End If

    Console.WriteLine("=== 終了処理シナリオ終了 ===")
    End Sub

    '==============================
    ' IT-01 シナリオ実行ヘルパ
    '==============================
    ''' <summary>
    ''' IT-01「起動時（タンキングデータ無し）」シナリオを一気に流す。
    ''' </summary>
    Public Sub RunIt01Scenario()
        ' 1) 起動処理コマンド
        SendLine("Call,AbtOpen,0")

        ' 2) 認証データ要求コマンド（試験用固定値）
        '    駅務機器情報：00112233445566778899
        '    認証データ送信日時：20250701
        SendLine("Call,AbtAuthenticationData,00112233445566778899,20250701")
    End Sub

    '==============================
    ' 判定要求：共通送信ロジック
    '==============================
    Private Sub SendJudgeRequest(execPermit As String, label As String)
        Console.WriteLine($"=== 判定要求シナリオ開始 （実行許可フラグ={execPermit} [{label}]） ===")

        Dim commandLine As String =
            $"Call,AbtTicketGateJudgment," &
            $"{procDir}," &          ' 引数 1: 処理方向
            $"{qrCode132}," &        ' 引数 2: QRコード (132桁)
            $"{qrTicket24}," &       ' 引数 3: QRチケット番号 (24桁)
            $"{reqTime16}," &        ' 引数 4: 要求日時 (16桁)
            $"{issueDisFlag}," &     ' 引数 5: 発行障害フラグ
            $"{appBailFlag}," &      ' 引数 6: 出場救済フラグ
            $"{offlineTktFlag}," &   ' 引数 7: オフライン改札機利用フラグ
            $"{execPermit}," &       ' 引数 8: 実行許可フラグ（ここだけ可変）
            $"{modelType}," &        ' 引数 9: 媒体種別
            $"{otherStaAppFlag}," &  ' 引数 10: 他駅入出場フラグ
            $"{bizOpRegCode}," &     ' 引数 11: 地域コード
            $"{bizOpUserCode}," &    ' 引数 12: ユーザコード
            $"{lineSec}," &          ' 引数 13: 線区
            $"{staOrder}"            ' 引数 14: 駅順

        SendLine(commandLine)

        Console.WriteLine("=== 判定要求シナリオ終了 ===")
    End Sub

    ''' <summary>
    ''' 現在の execPermitFlag 値で判定要求を1回だけ投げる（簡易テスト）。
    ''' </summary>
    Public Sub TestRequestJudgment()
        SendJudgeRequest(execPermitFlag, "CURRENT")
    End Sub

    ''' <summary>
    ''' 実行許可フラグ OFF（"00"）テスト用。
    ''' </summary>
    Public Sub TestRequestJudgment_ExecPermitOff()
        SendJudgeRequest("00", "OFF")

    End Sub

    ''' <summary>
    ''' 実行許可フラグ ON（"01"）テスト用。
    ''' </summary>
    Public Sub TestRequestJudgment_ExecPermitOn()
        SendJudgeRequest("01", "ON")

    End Sub

    '==============================
    ' タンキングテスト
    '==============================
    Public Sub TankingTest()
        CreateTankingTestData()

        Thread.Sleep(1000)

        SendLine("Call,AbtTicketGateJudgmentTanking,0,0")
    End Sub

    ''' <summary>
    ''' タンキング用バイナリCSVを1ファイル作成する。
    ''' 合計 108バイト = ヘッダー(4) + アプリケーションデータ(104)。
    ''' </summary>
    Public Sub CreateTankingTestData()
        Dim tankingDir As String = "C:\ABT\tanking"

        If Not Directory.Exists(tankingDir) Then
            Directory.CreateDirectory(tankingDir)
        End If

        ' 合計 108バイト = ヘッダー(4) + アプリケーションデータ(104)
        Dim payload(107) As Byte

        ' ヘッダー部分 (4バイト)
        payload(0) = &HA4  ' コマンドコード
        payload(1) = &H0   ' サブコード
        payload(2) = 104   ' レングス下位
        payload(3) = 0     ' レングス上位

        Dim offset As Integer = 4

        ' 1. 入出力データ部 (1バイト)
        payload(offset) = &H01  ' 0x01: 入場
        offset += 1

        ' 2. QRコード (66バイト)
        For i As Integer = 0 To 65
            payload(offset + i) = &H31  ' "1"で埋める
        Next
        offset += 66

        ' 3. QRチケット番号 (12バイト)
        For i As Integer = 0 To 11
            payload(offset + i) = &H32  ' "2"で埋める
        Next
        offset += 12

        ' 4. 要求日時 (8バイト, BCD想定)
        Dim reqTime() As Byte = {&H20, &H25, &H08, &H01, &H12, &H34, &H56, &H01}
        Buffer.BlockCopy(reqTime, 0, payload, offset, 8)
        offset += 8

        ' 5. 発行障害フラグ (1バイト)
        payload(offset) = &H00  ' 0x00: オンライン発行
        offset += 1

        ' 6. 出場救済フラグ (1バイト)
        payload(offset) = &H00  ' 0x00: 出場救済対象外
        offset += 1

        ' 7. オフライン改札機利用フラグ (1バイト)
        payload(offset) = &H01  ' 0x01: オフライン改札機利用なし
        offset += 1

        ' 8. 実行許可フラグ (1バイト)
        payload(offset) = &H00  ' 0x00: 実行許可オフ
        offset += 1

        ' 9. 媒体種別 (1バイト)
        payload(offset) = &H02  ' 0x02: QR
        offset += 1

        ' 10. 他駅入出場フラグ (1バイト)
        payload(offset) = &H00  ' 0x00: 他駅入出場処理を実施しない
        offset += 1

        ' 11. 事業者地域コード (1バイト)
        payload(offset) = &H01  ' サイバネコード設定
        offset += 1

        ' 12. 事業者ユーザコード (1バイト)
        payload(offset) = &H01  ' サイバネコード設定
        offset += 1

        ' 13. 線区 (1バイト)
        payload(offset) = &H01  ' サイバネコード設定
        offset += 1

        ' 14. 駅順 (1バイト)
        payload(offset) = &H01  ' サイバネコード設定
        offset += 1

        ' 15. 予備 (3バイト)
        For i As Integer = 0 To 2
            payload(offset + i) = &H00
        Next
        offset += 3

        ' 16. サム値 (4バイト)
        Dim sum As UInteger = 0
        For i As Integer = 4 To offset - 1  ' ヘッダーを除いたアプリケーションデータの合計
            sum += CUInt(payload(i))
        Next
        Buffer.BlockCopy(BitConverter.GetBytes(sum), 0, payload, offset, 4)

        ' ファイル名作成
        Dim stnInfo As String = "ABC"
        Dim timeStamp As String = DateTime.Now.ToString("yyyyMMddHHmmssfff")
        Dim fileName As String = $"{stnInfo}_{timeStamp}.csv"
        Dim filePath As String = Path.Combine(tankingDir, fileName)

        ' ファイル書き込み
        Try
            File.WriteAllBytes(filePath, payload)
            Console.WriteLine($"タンキングファイルを作成しました: {filePath}")
        Catch ex As IOException
            Console.WriteLine($"ファイル書き込みでエラー: {ex.Message}")
        End Try
    End Sub

End Class

Imports System
Imports System.IO
Imports System.IO.Pipes
Imports System.Text
Imports System.Threading
Imports System.Text.RegularExpressions
Imports System.Drawing

Imports ZXing
Imports ZXing.Common
Imports ZXing.Windows.Compatibility

''' <summary>
''' 制御アプリ模擬（ABTコントロールとの名前付きパイプ通信テスト用）
''' ・dt_ABTcontrolS にクライアントとして接続（コマンド送信：制御→ABT）
''' ・dt_ABTcontrolR をサーバとして待ち受け（Result/Event 受信：ABT→制御）
''' </summary>
Public Class ControlAppMock

    ' ★ ABT 側の実装に合わせたパイプ名
    Private Const CommandPipeName As String = "dt_ABTcontrolS"    ' 制御→ABT (ABT側 NamedPipeReceiver のサーバ)
    Private Const ResultPipeName As String = "dt_ABTcontrolR"     ' ABT→制御 (ABT側 NamedPipeSender のクライアント)

    ' ★ エンコーディング（SHIFT-JIS）
    Private ReadOnly commandEncoding As Encoding = Encoding.GetEncoding(932)
    Private ReadOnly resultEncoding As Encoding = Encoding.GetEncoding(932)

    ' パイプ実体
    Private commandClient As NamedPipeClientStream
    Private resultServer As NamedPipeServerStream
    Private resultReader As StreamReader

    Private recvThread As Thread
    Private recvCts As CancellationTokenSource

    '==============================
    ' Result待ち用（本番っぽくする）
    '==============================
    Private closeResultLine As String = Nothing
    Private closeResultEvent As New AutoResetEvent(False)

    Private openResultLine As String = Nothing
    Private openResultEvent As New AutoResetEvent(False)

    Private authResultLine As String = Nothing
    Private authResultEvent As New AutoResetEvent(False)

    '==============================
    ' 判定要求コマンド定義
    '==============================

    ' 132桁HEXのQRコード（args(1)）
    Private qrCode132 As String = ""

    ' ★ QRコードを設定する場合、QRチケット番号は 0x00（24桁）固定（仕様）
    Private qrTicket24 As String = New String("0"c, 24)

    ' 要求日時 (yyyyMMddHHmmssff) (args(3))
    Private reqTime16 As String = "2025120615150000"

    ' 処理方向 (args(0)) 例: "01"
    Private procDir As String = "01"

    ' 各フラグと駅情報コード (args(4)～args(13))
    Private issueDisFlag As String = "00"    ' 発行障害フラグ
    Private appBailFlag As String = "00"     ' 出場救済フラグ
    Private offlineTktFlag As String = "01"  ' オフライン改札機利用フラグ
    Private execPermitFlag As String = "00"  ' 実行許可フラグ（簡易テスト用デフォルト）
    Private modelType As String = "02"       ' 媒体種別 (01～04)
    Private otherStaAppFlag As String = "01" ' 他駅入出場フラグ（通常は00推奨）
    Private bizOpRegCode As String = "10"    ' 地域コード
    Private bizOpUserCode As String = "20"   ' ユーザコード
    Private lineSec As String = "30"         ' 線区
    Private staOrder As String = "40"        ' 駅順

    ' 認証データ要求で使う「駅務機器情報(20桁HEX)」保持（ログ/確認用）
    Private authStationInfoHex20 As String = ""

    ' ★ 外から受信メッセージを見られるようにイベントにする（ログでもOK）
    Public Event ReceivedLine(line As String)

    '==============================
    ' 接続開始
    '==============================
    Public Sub Start()
        ' 1) ABT→制御用のサーバ(dt_ABTcontrolR)を先に立てる
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

        ' 2) 制御→ABT用のクライアント(dt_ABTcontrolS)を接続
        commandClient = New NamedPipeClientStream(
            ".",
            CommandPipeName,
            PipeDirection.Out
        )
        Console.WriteLine($"[Mock] Connecting to ABT on {CommandPipeName} ...")
        commandClient.Connect()
        Console.WriteLine($"[Mock] Connected to {CommandPipeName}.")

        ' 3) 受信ループ開始
        recvCts = New CancellationTokenSource()
        recvThread = New Thread(AddressOf ReceiveLoop) With {.IsBackground = True}
        recvThread.Start()
    End Sub

    '==============================
    ' 終了処理
    '==============================
    Public Sub [Stop]()
        Try
            If recvCts IsNot Nothing Then recvCts.Cancel()
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
                If line Is Nothing Then Exit While ' 切断

                ' ★ Open の結果
                If line.StartsWith("Result,AbtOpen", StringComparison.OrdinalIgnoreCase) Then
                    openResultLine = line
                    openResultEvent.Set()
                End If

                ' ★ 認証の結果
                If line.StartsWith("Result,AbtAuthenticationData", StringComparison.OrdinalIgnoreCase) Then
                    authResultLine = line
                    authResultEvent.Set()
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

    '==============================
    ' Result行のOK判定（最後の値が 0 をOKとする想定）
    '==============================
    Private Function IsResultOk(line As String) As Boolean
        If String.IsNullOrWhiteSpace(line) Then Return False
        Dim parts = line.Split(","c)
        If parts.Length < 3 Then Return False
        Return parts(0).Equals("Result", StringComparison.OrdinalIgnoreCase) AndAlso
               parts(parts.Length - 1).Trim() = "0"
    End Function

    '==============================
    ' 終了処理シナリオ
    '==============================
    Public Sub RunCloseScenario(Optional timeoutSec As Integer = 3)
        Console.WriteLine("=== 終了処理シナリオ開始 ===")

        closeResultLine = Nothing
        closeResultEvent.Reset()

        SendLine("Call,AbtClose,0")

        If closeResultEvent.WaitOne(TimeSpan.FromSeconds(timeoutSec)) Then
            Console.WriteLine($"[CHECK] 終了レスポンス受信: {closeResultLine}")
        Else
            Console.WriteLine($"[CHECK] {timeoutSec}秒以内に Result,AbtClose が返ってきませんでした。")
        End If

        Console.WriteLine("=== 終了処理シナリオ終了 ===")
    End Sub

    '==============================
    ' IT-01（例：起動→認証）
    '==============================
    Public Sub RunIt01Scenario()
        SendLine("Call,AbtOpen,0")

        Dim authHex20 = "00010235FFE32F030500"
        SetStationInfoFromAuth(authHex20)

        SendLine($"Call,AbtAuthenticationData,{authHex20},20251225")
    End Sub

    '==============================
    ' 判定要求：共通送信
    '==============================
    Private Sub SendJudgeRequest(execPermit As String, label As String)
        Console.WriteLine($"=== 判定要求シナリオ開始 （実行許可フラグ={execPermit} [{label}]） ===")

        If String.IsNullOrWhiteSpace(qrCode132) Then
            Console.WriteLine("[Mock] qrCode132 が未設定(空)のため送信しません。LoadQrCodeFromImage などでセットしてください。")
            Return
        End If

        If qrCode132.Length <> 132 Then
            Console.WriteLine($"[Mock] QR長が132ではないため送信しません。len={qrCode132.Length}")
            Return
        End If

        ' ★ QRコードを設定する場合、QRチケット番号は 0x00（24桁）固定
        qrTicket24 = New String("0"c, 24)

        Dim commandLine As String =
            $"Call,AbtTicketGateJudgment," &
            $"{procDir}," &
            $"{qrCode132}," &
            $"{qrTicket24}," &
            $"{reqTime16}," &
            $"{issueDisFlag}," &
            $"{appBailFlag}," &
            $"{offlineTktFlag}," &
            $"{execPermit}," &
            $"{modelType}," &
            $"{otherStaAppFlag}," &
            $"{bizOpRegCode}," &
            $"{bizOpUserCode}," &
            $"{lineSec}," &
            $"{staOrder}"

        SendLine(commandLine)

        Console.WriteLine("=== 判定要求シナリオ終了 ===")
    End Sub

    ' 要求日時(yyyyMMddHHmmssff) を今時刻で作る（ff=ms/10）
    Private Function BuildReqTime16Now() As String
        Dim now = DateTime.Now
        Dim ff As Integer = now.Millisecond \ 10
        Return now.ToString("yyyyMMddHHmmss") & ff.ToString("D2")
    End Function

    '==============================
    ' 認証データ要求の駅務機器情報 → 判定要求の駅情報へ反映
    ' 20桁HEX(10byte) を想定
    ' bytes(2)=地域, bytes(3)=ユーザ, bytes(5)=線区, bytes(6)=駅順
    '==============================
    Public Sub SetStationInfoFromAuth(authHex20 As String)
        If String.IsNullOrWhiteSpace(authHex20) Then Throw New ArgumentException("authHex20 is empty")
        Dim s = authHex20.Trim()

        If Not Regex.IsMatch(s, "^[0-9A-Fa-f]{20}$") Then
            Throw New ArgumentException($"authHex20 must be 20 hex chars (10 bytes). actual='{s}'")
        End If

        authStationInfoHex20 = s.ToUpperInvariant()

        Dim bytes = HexToBytes(authStationInfoHex20) ' 10 bytes

        bizOpRegCode = bytes(2).ToString("X2")   ' 地域
        bizOpUserCode = bytes(3).ToString("X2")  ' ユーザ
        lineSec = bytes(5).ToString("X2")        ' 線区
        staOrder = bytes(6).ToString("X2")       ' 駅順

        Console.WriteLine($"[Mock] StationInfo set. REG={bizOpRegCode} USER={bizOpUserCode} LINE={lineSec} STA={staOrder}")
    End Sub

    Private Function HexToBytes(hex As String) As Byte()
        Dim n = hex.Length \ 2
        Dim b(n - 1) As Byte
        For i = 0 To n - 1
            b(i) = Convert.ToByte(hex.Substring(i * 2, 2), 16)
        Next
        Return b
    End Function

    '==============================
    ' 簡易テスト用
    '==============================
    Public Sub TestRequestJudgment()
        SendJudgeRequest(execPermitFlag, "CURRENT")
    End Sub

    Public Sub TestRequestJudgment_ExecPermitOff()
        SendJudgeRequest("00", "OFF")
    End Sub

    Public Sub TestRequestJudgment_ExecPermitOn()
        SendJudgeRequest("01", "ON")
    End Sub

    '==============================
    ' タンキング（必要ならこのまま）
    '==============================
    Public Sub TankingTest()
        CreateTankingTestData()
        Thread.Sleep(1000)
        SendLine("Call,AbtTicketGateJudgmentTanking,0,0")
    End Sub

    Public Sub CreateTankingTestData()
        Dim tankingDir As String = "C:\ABT\tanking"

        If Not Directory.Exists(tankingDir) Then
            Directory.CreateDirectory(tankingDir)
        End If

        ' 合計 108バイト = ヘッダー(4) + アプリケーションデータ(104)
        Dim payload(107) As Byte

        ' ヘッダー (4)
        payload(0) = &HA4
        payload(1) = &H0
        payload(2) = 104
        payload(3) = 0

        Dim offset As Integer = 4

        ' 1. 入出力データ部 (1)
        payload(offset) = &H1 : offset += 1

        ' 2. QRコード (66)
        For i As Integer = 0 To 65
            payload(offset + i) = &H31
        Next
        offset += 66

        ' 3. QRチケット番号 (12)
        For i As Integer = 0 To 11
            payload(offset + i) = &H32
        Next
        offset += 12

        ' 4. 要求日時 (8, BCD想定)
        Dim reqTime() As Byte = {&H20, &H25, &H8, &H1, &H12, &H34, &H56, &H1}
        Buffer.BlockCopy(reqTime, 0, payload, offset, 8)
        offset += 8

        ' 5-14 各種 (10)
        payload(offset) = &H0 : offset += 1 ' 発行障害
        payload(offset) = &H0 : offset += 1 ' 出場救済
        payload(offset) = &H1 : offset += 1 ' オフライン
        payload(offset) = &H0 : offset += 1 ' 実行許可
        payload(offset) = &H2 : offset += 1 ' 媒体
        payload(offset) = &H0 : offset += 1 ' 他駅
        payload(offset) = &H1 : offset += 1 ' 地域
        payload(offset) = &H1 : offset += 1 ' ユーザ
        payload(offset) = &H1 : offset += 1 ' 線区
        payload(offset) = &H1 : offset += 1 ' 駅順

        ' 15. 予備 (3)
        For i As Integer = 0 To 2
            payload(offset + i) = &H0
        Next
        offset += 3

        ' 16. サム (4)
        Dim sum As UInteger = 0
        For i As Integer = 4 To offset - 1
            sum += CUInt(payload(i))
        Next
        Buffer.BlockCopy(BitConverter.GetBytes(sum), 0, payload, offset, 4)

        ' ファイル出力
        Dim stnInfo As String = "ABC"
        Dim timeStamp As String = DateTime.Now.ToString("yyyyMMddHHmmssfff")
        Dim fileName As String = $"{stnInfo}_{timeStamp}.csv"
        Dim filePath As String = Path.Combine(tankingDir, fileName)

        Try
            File.WriteAllBytes(filePath, payload)
            Console.WriteLine($"タンキングファイルを作成しました: {filePath}")
        Catch ex As IOException
            Console.WriteLine($"ファイル書き込みでエラー: {ex.Message}")
        End Try
    End Sub

    '========================================================
    ' QR画像 → qrCode132(=132桁HEX) セット（あなたのロジックそのまま）
    '========================================================
    Public Sub LoadQrCodeFromImage(filePath As String)
        Try
            If Not File.Exists(filePath) Then
                Console.WriteLine($"[Mock] 画像が見つかりません: {filePath}")
                Return
            End If

            Using bmp As New Bitmap(filePath)
                Dim reader As New ZXing.BarcodeReader() With {
                    .AutoRotate = True,
                    .TryInverted = True
                }

                Dim result = reader.Decode(bmp)
                If result Is Nothing Then
                    Console.WriteLine("[Mock] QRのデコードに失敗しました（result=None）")
                    Return
                End If

                Dim hex As String = ExtractQrAsHex132(result)
                If String.IsNullOrEmpty(hex) Then
                    Console.WriteLine("[Mock] QRからHEXを生成できませんでした（RawBytesも取得不可）")
                    Return
                End If

                Me.qrCode132 = hex

                If Me.qrCode132.Length <> 132 Then
                    Console.WriteLine($"[Mock] QR長が132ではありません。len={Me.qrCode132.Length}")
                Else
                    Console.WriteLine("[Mock] QR読み取り成功 len=132 (HEX)")
                End If
            End Using

        Catch ex As Exception
            Console.WriteLine($"[Mock] QR画像読み込み失敗: {ex.Message}")
        End Try
    End Sub

    ' 132桁HEXを取り出す
    Private Function ExtractQrAsHex132(r As ZXing.Result) As String
        ' 1) Textが132桁HEXなら採用（ASCII埋め込みQR）
        Dim t As String = If(r.Text, "").Trim()
        If Regex.IsMatch(t, "^[0-9A-Fa-f]{132}$") Then
            Return t.ToUpperInvariant()
        End If

        ' 2) BYTE_SEGMENTS優先（バイナリQR）
        Try
            If r.ResultMetadata IsNot Nothing AndAlso r.ResultMetadata.ContainsKey(ZXing.ResultMetadataType.BYTE_SEGMENTS) Then
                Dim segObj = r.ResultMetadata(ZXing.ResultMetadataType.BYTE_SEGMENTS)
                Dim segs = TryCast(segObj, System.Collections.Generic.IList(Of Byte()))
                If segs IsNot Nothing AndAlso segs.Count > 0 Then
                    Dim bytes = segs(0)
                    Return BytesToHex(bytes)
                End If
            End If
        Catch
        End Try

        ' 3) RawBytes
        Dim raw As Byte() = r.RawBytes
        If raw Is Nothing OrElse raw.Length = 0 Then
            Return ""
        End If

        Return BytesToHex(raw)
    End Function

    ' バイト列 → HEX（大文字、ハイフン無し）
    Private Function BytesToHex(bytes As Byte()) As String
        Dim sb As New StringBuilder(bytes.Length * 2)
        For Each b In bytes
            sb.Append(b.ToString("X2"))
        Next
        Return sb.ToString()
    End Function

End Class

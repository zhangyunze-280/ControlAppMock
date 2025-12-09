Module Program
    Sub Main()
        Dim mock As New ControlAppMock()

        ' ★ ここにブレークポイント①を置くと、Start() の中の接続の様子を追える
        mock.Start()

        ' ★ ここにブレークポイント②：IT-01 の入力シーケンスを流す直前
        mock.RunIt01Scenario()

        System.Threading.Thread.Sleep(5000)

        mock.TestRequestJudgment()

        TankingTest()

        Console.WriteLine("IT-01 シナリオ送信完了。Result/Event を確認してください。")
        Console.WriteLine("何かキーを押すと終了します...")
        Console.ReadKey()

        mock.Stop()
    End Sub
End Module

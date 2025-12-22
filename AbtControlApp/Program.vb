Module Program
    Sub Main()
        Dim mock As New ControlAppMock()

        ' ★ ここにブレークポイント①を置くと、Start() の中の接続の様子を追える
        mock.Start()

        ' ★ ここにブレークポイント②：IT-01 の入力シーケンスを流す直前
        mock.RunIt01Scenario()

        System.Threading.Thread.Sleep(5000)
        
        ' ★ 実行許可フラグ OFF（オフラインの時、タンキングなし → Event結果=0想定）
       'mock.TestRequestJudgment_ExecPermitOff()

        '旅客通過時間を模擬
        'System.Threading.Thread.Sleep(800)

    ' ★ 実行許可フラグ ON（オフラインの時、タンキングあり → Event結果=5想定）
       'mock.TestRequestJudgment_ExecPermitOn()


      ' ★ 実行許可フラグ OFF（オフラインの時、タンキングなし → Event結果=0想定）
       'mock.TestRequestJudgment_ExecPermitOff()


        '旅客通過時間を模擬
        'System.Threading.Thread.Sleep(800)


    ' ★ 実行許可フラグ ON（オフラインの時、タンキングあり → Event結果=5想定）
       'mock.TestRequestJudgment_ExecPermitOn()

        'mock.TestRequestJudgment()

        'タンキング
         mock.TankingTest()
    
        System.Threading.Thread.Sleep(65000)
        'mock.TestRequestJudgment_ExecPermitOn()

        mock.RunCloseScenario()  ' 終了処理

        Console.WriteLine("IT-01 シナリオ送信完了。Result/Event を確認してください。")
        Console.WriteLine("何かキーを押すと終了します...")
        Console.ReadKey()


        mock.Stop()
    End Sub
End Module
 
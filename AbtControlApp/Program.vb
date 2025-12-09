Imports System.Threading
Module Program
    Sub Main()
        Dim mock As New ControlAppMock()

        mock.Start()

        ' 起動シナリオ（任意）
        mock.RunIt01Scenario()
        Thread.Sleep(3000)

    ' ★ 実行許可フラグ OFF（タンキングなし → Event結果=0想定）
        mock.TestRequestJudgment_ExecPermitOff()

    ' ★ 実行許可フラグ ON（タンキングあり → Event結果=5想定）
        mock.TestRequestJudgment_ExecPermitOn()
        Thread.Sleep(5000)

    ' ★ 実行許可フラグ ON（タンキングあり → Event結果=5想定）
        mock.TestRequestJudgment_ExecPermitOn()
    ' ★ 実行許可フラグ OFF（タンキングなし → Event結果=0想定）
        mock.TestRequestJudgment_ExecPermitOff()

        Console.WriteLine("判定要求シナリオ送信完了。Result/Event を確認してください。")
        Console.WriteLine("何かキーを押すと終了します...")
        Console.ReadKey()

        mock.Stop()
    End Sub
End Module
' ParsGate Launcher - Runs ParsGate via elevated scheduled task
' This avoids UAC prompts after initial installation
' Usage: wscript.exe ParsGate-Launcher.vbs

Option Explicit

Dim objShell, taskName, exePath, result

taskName = "ParsGateElevatedTask"
Set objShell = CreateObject("WScript.Shell")

' Run the scheduled task which executes with highest privileges
On Error Resume Next
result = objShell.Run("schtasks.exe /run /tn """ & taskName & """", 0, False)
On Error GoTo 0

If Err.Number <> 0 Then
    ' Fallback: try direct execution if task doesn't exist
    exePath = objShell.ExpandEnvironmentStrings("%ProgramFiles%\ParsGate\ParsGate.exe")
    If CreateObject("Scripting.FileSystemObject").FileExists(exePath) Then
        objShell.Run """" & exePath & """", 1, False
    End If
End If

Set objShell = Nothing
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W {
 [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(int f,int x,int y,int d,int e);
}
"@
$exe = "C:\Users\BCM-DEV\source\repos\Reverse Engineer\MiniCut\bin\Debug\net8.0-windows\MiniCut.exe"
$dump = "$env:TEMP\ui_state.json"
function Start-App($proj) {
  Get-Process MiniCut -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-Item $dump -ErrorAction SilentlyContinue
  Remove-Item Env:MINICUT_CURVE,Env:MINICUT_TRACE,Env:MINICUT_SELECT,Env:MINICUT_SPLITAT -ErrorAction SilentlyContinue
  $env:MINICUT_AUTOPLAY = $proj; $env:MINICUT_DUMP = $dump
  $global:p = Start-Process $exe -PassThru; Start-Sleep 8
  $global:h = (Get-Process MiniCut).MainWindowHandle; [W]::ShowWindow($h, 3) | Out-Null; Start-Sleep 2
  $pt = New-Object W+PT; [W]::ClientToScreen($h, [ref]$pt) | Out-Null; $global:ox = $pt.X; $global:oy = $pt.Y
}
# input posted straight to the window (works while another app has the focus); coordinates are SCREEN coordinates
function L($x,$y) { $cx = $x - $global:ox; $cy = $y - $global:oy; [IntPtr](($cy -shl 16) -bor ($cx -band 0xFFFF)) }
function PClick($x,$y) { [W]::PostMessage($h,0x200,[IntPtr]0,(L $x $y))|Out-Null; Start-Sleep -Milliseconds 80; [W]::PostMessage($h,0x201,[IntPtr]1,(L $x $y))|Out-Null; Start-Sleep -Milliseconds 80; [W]::PostMessage($h,0x202,[IntPtr]0,(L $x $y))|Out-Null; Start-Sleep -Milliseconds 400 }
function PDrag($x1,$y1,$x2,$y2) {
  [W]::PostMessage($h,0x200,[IntPtr]0,(L $x1 $y1))|Out-Null; Start-Sleep -Milliseconds 100; [W]::PostMessage($h,0x201,[IntPtr]1,(L $x1 $y1))|Out-Null; Start-Sleep -Milliseconds 100
  for($i=1;$i -le 14;$i++){ $x=[int]($x1+($x2-$x1)*$i/14); $y=[int]($y1+($y2-$y1)*$i/14); [W]::PostMessage($h,0x200,[IntPtr]1,(L $x $y))|Out-Null; Start-Sleep -Milliseconds 40 }
  [W]::PostMessage($h,0x202,[IntPtr]0,(L $x2 $y2))|Out-Null; Start-Sleep -Milliseconds 600 }
function PKey($vk) { [W]::PostMessage($h,0x100,[IntPtr]$vk,[IntPtr]1)|Out-Null; Start-Sleep -Milliseconds 60; [W]::PostMessage($h,0x101,[IntPtr]$vk,[IntPtr]0xC0000001)|Out-Null; Start-Sleep -Milliseconds 500 }
function PWheel($x,$y,$delta,$mods=0) { $pt2 = New-Object W+PT; $pt2.X=$x; $pt2.Y=$y; [W]::PostMessage($h,0x20A,[IntPtr](($delta -shl 16) -bor $mods),[IntPtr](($y -shl 16) -bor ($x -band 0xFFFF)))|Out-Null; Start-Sleep -Milliseconds 400 }
function State { Start-Sleep -Milliseconds 300; Get-Content $dump -Raw | ConvertFrom-Json }
function Shot($n) { $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height; [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); $bmp.Save("$env:TEMP\$n.png") }
function Stop-App { Get-Process MiniCut -ErrorAction SilentlyContinue | Stop-Process -Force }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# --- UI Automation: find a button by its text, click its centre (real hit-testing) or invoke it
function Btns { $root = [System.Windows.Automation.AutomationElement]::FromHandle($global:h); $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) }
function FindBtn($name, $nth = 0) { $m = @(); foreach ($b in (Btns)) { if ($b.Current.Name -eq $name -and $b.Current.BoundingRectangle.Width -gt 0 -and -not $b.Current.IsOffscreen) { $m += $b } }; if ($m.Count -gt $nth) { $m[$nth] } else { $null } }
function BClick($name, $nth = 0) { $b = FindBtn $name $nth; if (-not $b) { return "MISSING:$name" }; $r = $b.Current.BoundingRectangle; PClick ([int]($r.X + $r.Width/2)) ([int]($r.Y + $r.Height/2)); "ok" }
function BCenter($name, $nth = 0) { $b = FindBtn $name $nth; if ($b) { $r = $b.Current.BoundingRectangle; ,@([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2)) } }
# posted mouse messages cannot make WPF buttons "IsMouseOver", so buttons are pressed through UI Automation (calls the same Click handler)
function BInvoke($name, $nth = 0) { $b = FindBtn $name $nth; if (-not $b) { return "MISSING:$name" }; try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500; "ok" } catch { "ERR:" + $_.Exception.Message } }

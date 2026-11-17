using System; using System.Runtime.InteropServices;
class T { [DllImport("user32.dll", SetLastError=true)] static extern bool SystemParametersInfo(uint a, uint b, byte[] c, uint d);
 static void Main(){ var b=new byte[48]; BitConverter.GetBytes(1u).CopyTo(b,0);
  bool ok=SystemParametersInfo(0xAE,48,b,0); Console.WriteLine("get ok="+ok+" err="+Marshal.GetLastWin32Error());
  uint f1=BitConverter.ToUInt32(b,12), f2=BitConverter.ToUInt32(b,16);
  Console.WriteLine("ver="+BitConverter.ToUInt32(b,0)+" maxContacts="+BitConverter.ToUInt32(b,4)+" flags1=0x"+f1.ToString("X")+" flags2=0x"+f2.ToString("X")+" sens="+BitConverter.ToUInt32(b,20)+" speed="+BitConverter.ToUInt32(b,24));
  string[] n1={"present","legacyPresent","extMouse","enabled","active","feedbackSup","clickForceSup"}; for(int i=0;i<n1.Length;i++) Console.Write(n1[i]+"="+((f1>>i)&1)+" "); Console.WriteLine();
  string[] n2={"activeWithMouse","feedback","tap","tapDrag","twoFingerTap","rightClickZone","accel","pan","zoom","scrollRev"}; for(int i=0;i<n2.Length;i++) Console.Write(n2[i]+"="+((f2>>i)&1)+" "); Console.WriteLine(); }}

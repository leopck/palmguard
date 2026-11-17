using System; using System.Runtime.InteropServices; using System.Collections.Generic;
class P {
 [StructLayout(LayoutKind.Sequential)] struct RID { public IntPtr h; public uint t; }
 [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] RID[] l, ref uint n, uint sz);
 [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(IntPtr h, uint c, IntPtr d, ref uint sz);
 [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr p);
 static void Main(){ uint n=0; GetRawInputDeviceList(null, ref n, 16); var l=new RID[n]; GetRawInputDeviceList(l, ref n, 16);
  foreach(var d in l){ uint sz=0; GetRawInputDeviceInfo(d.h,0x20000007,IntPtr.Zero,ref sz); var nb=Marshal.AllocHGlobal((int)sz*2+2); GetRawInputDeviceInfo(d.h,0x20000007,nb,ref sz); var name=Marshal.PtrToStringUni(nb);
   if(name.IndexOf("Col02",StringComparison.OrdinalIgnoreCase)<0||name.IndexOf("BLTP",StringComparison.OrdinalIgnoreCase)<0) continue;
   sz=0; GetRawInputDeviceInfo(d.h,0x20000005,IntPtr.Zero,ref sz); var pp=Marshal.AllocHGlobal((int)sz); GetRawInputDeviceInfo(d.h,0x20000005,pp,ref sz);
   ushort c=256; var vc=new byte[72*c]; HidP_GetValueCaps(0,vc,ref c,pp); var seen=new HashSet<string>();
   for(int i=0;i<c;i++){int o=i*72; string s=string.Format("page=0x{0:X2} usage=0x{1:X2} link={2} log=[{3},{4}] phys=[{5},{6}] unit=0x{7:X} exp={8}", BitConverter.ToUInt16(vc,o), BitConverter.ToUInt16(vc,o+56), BitConverter.ToUInt16(vc,o+6), BitConverter.ToInt32(vc,o+40), BitConverter.ToInt32(vc,o+44), BitConverter.ToInt32(vc,o+48), BitConverter.ToInt32(vc,o+52), BitConverter.ToUInt32(vc,o+36), BitConverter.ToUInt32(vc,o+32)); if(BitConverter.ToUInt16(vc,o+6)<=1 && seen.Add(s)) Console.WriteLine(s);} } } }

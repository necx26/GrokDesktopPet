using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrokPet;
public class ConnectionProfile {
    public string Id{get;set;}=Guid.NewGuid().ToString("N");
    public string Name{get;set;}="本地 DeepSeek R1";
    public string Provider{get;set;}="llama.cpp · R1 快速模式";
    public string Endpoint{get;set;}="http://127.0.0.1:8080";
    public string Model{get;set;}="";
    public bool NativeR1{get;set;}=true;
    public bool JsonMode{get;set;}=true;
    public override string ToString()=>Name;
}
public class Settings {
    public string Endpoint{get;set;}="http://127.0.0.1:8080"; // v1 migration only
    public double X{get;set;}=double.NaN;public double Y{get;set;}=double.NaN;
    public double Size{get;set;}=144;
    public double Opacity{get;set;}=1;
    public double Motion{get;set;}=1;
    public double Elasticity{get;set;}=1;
    public double GazeRange{get;set;}=1;
    public double BubbleSeconds{get;set;}=7;
    public double BubbleFontSize{get;set;}=14;
    public bool AlwaysOnTop{get;set;}=true;
    public bool ReducedMotion{get;set;}
    public bool EyeTracking{get;set;}=true;
    public bool ReactToClick{get;set;}=true;
    public bool ReactToDrag{get;set;}=true;
    public string PetName{get;set;}="Grok";
    public string Personality{get;set;}="俏皮、亲近、有自己的小脾气，像真实的小伙伴。";
    public string ExtraPrompt{get;set;}="";
    public string Language{get;set;}="中文";
    public int MemoryTurns{get;set;}=4;
    public double Temperature{get;set;}=.75;
    public int MaxTokens{get;set;}=480;
    public int TimeoutSeconds{get;set;}=45;
    public bool ShowPanelOnStart{get;set;}=true;
    public bool AutoStartLocalServer{get;set;}=true;
    public bool OpenChatOnLaunch{get;set;}=true;
    public string ServerExecutable{get;set;}="";
    public string ModelFile{get;set;}="";
    public List<ConnectionProfile> Connections{get;set;}=[];
    public string ActiveConnectionId{get;set;}="";
    [JsonIgnore]public ConnectionProfile Active=>Connections.FirstOrDefault(p=>p.Id==ActiveConnectionId)??Connections.First();
    public void Normalize(){
        if(Connections.Count==0){var profile=new ConnectionProfile{Endpoint=Endpoint};Connections.Add(profile);ActiveConnectionId=profile.Id;}
        if(Connections.Count>30)throw new InvalidDataException("最多保留 30 套连接配置。");
        if(!Connections.Any(p=>p.Id==ActiveConnectionId))ActiveConnectionId=Connections[0].Id;
        double Safe(double value,double fallback,double lo,double hi)=>double.IsFinite(value)?Math.Clamp(value,lo,hi):fallback;
        Size=Safe(Size,144,96,216);Opacity=Safe(Opacity,1,.35,1);Motion=Safe(Motion,1,0,1.5);Elasticity=Safe(Elasticity,1,0,1.5);GazeRange=Safe(GazeRange,1,.2,1.1);BubbleSeconds=Safe(BubbleSeconds,7,3,20);BubbleFontSize=Safe(BubbleFontSize,14,12,20);Temperature=Safe(Temperature,.75,0,1.5);
        MemoryTurns=Math.Clamp(MemoryTurns,0,8);MaxTokens=Math.Clamp(MaxTokens,128,2048);TimeoutSeconds=Math.Clamp(TimeoutSeconds,5,120);
        if(string.IsNullOrWhiteSpace(PetName))PetName="Grok";PetName=PetName[..Math.Min(24,PetName.Length)];
        if(Connections.Any(c=>!ValidEndpoint(c.Endpoint)))throw new InvalidDataException("服务地址需要是完整的 http:// 或 https:// 地址，不能包含密码或查询参数。");
    }
    public static bool ValidEndpoint(string text)=>Uri.TryCreate(text.Trim(),UriKind.Absolute,out var uri)&&(uri.Scheme=="http"||uri.Scheme=="https")&&string.IsNullOrEmpty(uri.UserInfo)&&string.IsNullOrEmpty(uri.Query)&&string.IsNullOrEmpty(uri.Fragment);
    public Settings Clone()=>JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this,Program.JsonOptions),Program.JsonOptions)!;
}
// Windows Credential Manager: secrets never enter settings, logs or exported JSON.
public static class CredentialVault {
    const string Prefix="GrokDesktopPet/";
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Credential {
        public uint Flags,Type;public string TargetName;public string? Comment;public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;public uint CredentialBlobSize;public IntPtr CredentialBlob;public uint Persist,AttributeCount;public IntPtr Attributes;public string? TargetAlias;public string? UserName;
    }
    [DllImport("advapi32.dll",EntryPoint="CredWriteW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Write(ref Credential value,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredReadW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Read(string target,uint type,uint flags,out IntPtr value);
    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool Delete(string target,uint type,uint flags);
    [DllImport("advapi32.dll")]static extern void CredFree(IntPtr value);
    public static string Get(string id){if(!Read(Prefix+id,1,0,out var ptr))return "";try{var c=Marshal.PtrToStructure<Credential>(ptr);return Marshal.PtrToStringUni(c.CredentialBlob,(int)c.CredentialBlobSize/2)??"";}finally{CredFree(ptr);}}
    public static void Set(string id,string secret){if(string.IsNullOrWhiteSpace(secret)){Delete(Prefix+id,1,0);return;}var bytes=Encoding.Unicode.GetBytes(secret);if(bytes.Length>5000)throw new InvalidDataException("API Key 太长。");var ptr=Marshal.AllocHGlobal(bytes.Length);try{Marshal.Copy(bytes,0,ptr,bytes.Length);var c=new Credential{Type=1,TargetName=Prefix+id,CredentialBlobSize=(uint)bytes.Length,CredentialBlob=ptr,Persist=2,UserName="GrokDesktopPet"};if(!Write(ref c,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}finally{for(int i=0;i<bytes.Length;i++)Marshal.WriteByte(ptr,i,0);Marshal.FreeHGlobal(ptr);Array.Clear(bytes);}}
}

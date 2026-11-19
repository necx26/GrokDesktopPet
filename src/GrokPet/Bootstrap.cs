using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace GrokPet;
public static class Bootstrap {
    public static async Task<bool> EnsureService(){
        var settings=Program.Config;var profile=settings.Active;
        if(!profile.NativeR1||!Uri.TryCreate(profile.Endpoint,UriKind.Absolute,out var uri)||!uri.IsLoopback||uri.Port!=8080)return false;
        string root=uri.GetLeftPart(UriPartial.Authority);
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
        async Task<bool> Healthy(){try{using var response=await client.GetAsync(root+"/health");return response.IsSuccessStatusCode;}catch{return false;}}
        if(await Healthy())return true;
        if(!settings.AutoStartLocalServer)return false;
        bool occupied=false;try{using var socket=new TcpClient();await socket.ConnectAsync(uri.Host,uri.Port).WaitAsync(TimeSpan.FromSeconds(2));occupied=true;}catch{}
        if(!occupied){
            string exe=settings.ServerExecutable,model=settings.ModelFile;
            if(string.IsNullOrWhiteSpace(exe))exe=Path.Combine(AppContext.BaseDirectory,"llama.cpp","llama-server.exe");
            if(string.IsNullOrWhiteSpace(model))model=Path.Combine(AppContext.BaseDirectory,"models","model.gguf");
            if(!File.Exists(exe)||!File.Exists(model)){Program.Log("startup","local service files not configured");return false;}
            var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(exe)!};
            foreach(string arg in new[]{"-m",model,"-ngl","99","-c","8192","-np","1","--jinja","--flash-attn","on","--cache-type-k","q8_0","--cache-type-v","q8_0","--temp","0.6","--top-p","0.95","--top-k","0","--min-p","0","--host","127.0.0.1","--port","8080"})info.ArgumentList.Add(arg);
            Process.Start(info);Program.Log("startup","local model service started");
        }
        for(int i=0;i<90;i++){await Task.Delay(1000);if(await Healthy())return true;}
        Program.Log("startup","local service readiness timeout");return false;
    }
    public static void OpenChat(){
        try{string shortcut=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"llama-ui.lnk");Process.Start(new ProcessStartInfo(File.Exists(shortcut)?shortcut:"http://127.0.0.1:8080/"){UseShellExecute=true});}catch{Program.Log("startup","chat window launch failed");}
    }
}

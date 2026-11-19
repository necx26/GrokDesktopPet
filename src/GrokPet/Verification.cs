using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GrokPet;
// A loopback-only fake compatible API exercises HTTP format and error handling.
// No live cloud account, credential or external network is used by these checks.
public static class Verification {
    public static async Task<object> CompatibleApi(){
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var original=Program.Config;var profile=new ConnectionProfile{Name="test",Provider="自定义兼容 API",Endpoint=$"http://127.0.0.1:{port}/v1",Model="test-pet",NativeR1=false};
        const string key="local-verification-not-a-real-key";
        var errors=new List<string>();int handled=0;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var server=Task.Run(async()=>{
            while(handled<4){using var socket=await listener.AcceptTcpClientAsync(stop.Token);using var stream=socket.GetStream();using var buffer=new MemoryStream();byte[] chunk=new byte[8192];int split=-1,length=0;
                while(true){int read=await stream.ReadAsync(chunk,stop.Token);if(read==0)throw new EndOfStreamException();buffer.Write(chunk,0,read);string raw=Encoding.UTF8.GetString(buffer.ToArray());split=raw.IndexOf("\r\n\r\n",StringComparison.Ordinal);if(split<0)continue;string header=raw[..split];length=header.Split("\r\n").Where(l=>l.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).Select(l=>int.Parse(l.Split(':')[1].Trim())).FirstOrDefault();int headerBytes=Encoding.UTF8.GetByteCount(raw[..(split+4)]);if(buffer.Length>=headerBytes+length)break;}
                string request=Encoding.UTF8.GetString(buffer.ToArray());int boundary=request.IndexOf("\r\n\r\n",StringComparison.Ordinal);string head=request[..boundary],body=request[(boundary+4)..];
                if(!head.Contains("Authorization: Bearer "+key,StringComparison.OrdinalIgnoreCase))errors.Add("Authorization header missing");
                string response;int code=200;
                if(handled==0){if(!head.StartsWith("GET /v1/models "))errors.Add("Model list path");response="{\"data\":[{\"id\":\"test-pet\"}]}";}
                else if(handled==3){code=401;response="{}";}
                else{using var json=JsonDocument.Parse(body);if(!head.StartsWith("POST /v1/chat/completions "))errors.Add("Completion path");if(json.RootElement.GetProperty("model").GetString()!="test-pet")errors.Add("Model selection");if(json.RootElement.TryGetProperty("response_format",out _) != (handled==1))errors.Add("JSON mode flag");var reply=new PetReply{say="兼容接口测试通过",frames=[new(){yaw=12,pitch=10,left=1,right=.3,ms=500}]};response=JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=JsonSerializer.Serialize(reply)}}}});}
                byte[] payload=Encoding.UTF8.GetBytes(response),headers=Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code==200?"OK":"Unauthorized")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(headers,stop.Token);await stream.WriteAsync(payload,stop.Token);handled++;
            }
        },stop.Token);
        try{
            CredentialVault.Set(profile.Id,key);if(CredentialVault.Get(profile.Id)!=key)throw new Exception("Credential vault round-trip failed");
            var config=original.Clone();config.Connections=[profile];config.ActiveConnectionId=profile.Id;config.Normalize();Program.Config=config;
            var models=await ConnectionTools.Models(profile,key,stop.Token);if(models.Single()!="test-pet")throw new Exception("Model list decode failed");
            var brain=new Brain();var first=await brain.React("测试互动",stop.Token);if(first.frames.Count!=1)throw new Exception("Compatible response decode failed");
            profile.JsonMode=false;await brain.React("无 JSON 模式测试",stop.Token);
            bool rejected=false;try{await brain.React("鉴权错误测试",stop.Token);}catch(System.Net.Http.HttpRequestException ex){rejected=ex.Message.Contains("API Key");}if(!rejected)throw new Exception("401 did not produce useful error");
            await server;if(errors.Count>0)throw new Exception(string.Join("; ",errors));
            if(JsonSerializer.Serialize(config,Program.JsonOptions).Contains(key))throw new Exception("Secret leaked into configuration");
            return new{name="compatible_api_and_credentials",ok=true,requests=handled,jsonMode=true,plainMode=true,authorizationFailure=true,credentialRoundTrip=true};
        }finally{Program.Config=original;stop.Cancel();listener.Stop();CredentialVault.Set(profile.Id,"");try{await server;}catch{}}
    }
}

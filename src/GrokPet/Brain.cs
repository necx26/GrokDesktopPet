using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrokPet;
public record MotorFrame {
    public double yaw{get;set;} public double pitch{get;set;}=10;
    public double left{get;set;}=1;public double right{get;set;}=1;
    public double dx{get;set;} public double dy{get;set;}
    public double stretch{get;set;}=1;public double tilt{get;set;}
    public int ms{get;set;}=600;
    public void Normalize(){yaw=Math.Clamp(yaw,-78,78);pitch=Math.Clamp(pitch,-28,45);left=Math.Clamp(left,.025,1.2);right=Math.Clamp(right,.025,1.2);dx=Math.Clamp(dx,-26,26);dy=Math.Clamp(dy,-20,20);stretch=Math.Clamp(stretch,.78,1.22);tilt=Math.Clamp(tilt,-22,22);ms=Math.Clamp(ms,200,1800);}
}
public record PetReply {public string say{get;set;}="";public List<MotorFrame> frames{get;set;}=[];}
public class Brain {
    readonly HttpClient client=new(){Timeout=System.Threading.Timeout.InfiniteTimeSpan};
    readonly List<string> memory=[];
    int requestCount;
    public int RequestCount=>requestCount;
    public string Endpoint{get=>Program.Config.Active.Endpoint;set=>Program.Config.Active.Endpoint=value;}
    public string LastRaw{get;private set;}="";
    public void ClearMemory()=>memory.Clear();
    public async Task<PetReply> React(string interaction,CancellationToken ct) {
        Interlocked.Increment(ref requestCount);
        var config=Program.Config.Clone();var profile=config.Active;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));ct=timeout.Token;
        var number=new{type="number"};
        var schema=new {type="object",properties=new {say=new{type="string",maxLength=65},frames=new{type="array",minItems=1,maxItems=3,items=new{type="object",properties=new{yaw=number,pitch=number,left=number,right=number,dx=number,dy=number,stretch=number,tilt=number,ms=new{type="integer"}},required=new[]{"yaw","pitch","left","right","dx","dy","stretch","tilt","ms"},additionalProperties=false}}},required=new[]{"say","frames"},additionalProperties=false};
        string instruction=$"你是有性格的白色小球桌宠{config.PetName}，不是聊天客服。只在用户互动时回应，性格：{config.Personality}。用{config.Language}回应，不重复，台词最多45字。你自己决定表情和动作，直接输出JSON：say和frames。frames为1到3个动作关键帧，每帧字段：yaw左右转头(-65到65)，pitch抬头(-25到40，默认10)，left和right眼睛开合(0.03到1，默认1)，dx和dy身体偏移(-20到20)，stretch纵向伸缩(0.8到1.2，默认1)，tilt倾斜(-18到18)，ms动作时长(250到1000)。dy负数向上；可以眨眼、眯眼、歪头、小跳，按互动情境自主组合。不讲参数，不讲分析，不假装已看过屏幕或执行电脑操作。"+config.ExtraPrompt;
        string current="最近互动："+string.Join("；",memory.TakeLast(config.MemoryTurns))+"。现在："+interaction;
        object request;string url;
        if(profile.NativeR1){
            string prompt="<｜begin▁of▁sentence｜>"+instruction+"<｜User｜>"+current+"<｜Assistant｜><think>\n</think>\n";
            request=new{prompt,n_predict=config.MaxTokens,temperature=config.Temperature,top_p=.95,top_k=40,min_p=.02,repeat_penalty=1.02,stream=false,json_schema=schema,cache_prompt=true};url=profile.Endpoint.TrimEnd('/')+"/completion";
        }else{
            if(string.IsNullOrWhiteSpace(profile.Model))throw new InvalidDataException("请在控制中心选择或填写模型 ID。");
            var data=new Dictionary<string,object>{["model"]=profile.Model,["messages"]=new[]{new{role="system",content=instruction},new{role="user",content=current}},["temperature"]=config.Temperature,["max_tokens"]=config.MaxTokens,["stream"]=false};
            if(profile.JsonMode)data["response_format"]=new{type="json_object"};
            if(profile.Provider=="DeepSeek API")data["thinking"]=new{type="disabled"};
            request=data;url=profile.Endpoint.TrimEnd('/')+"/chat/completions";
        }
        using var http=new HttpRequestMessage(HttpMethod.Post,url){Content=new StringContent(JsonSerializer.Serialize(request),Encoding.UTF8,"application/json")};
        var key=CredentialVault.Get(profile.Id);if(!string.IsNullOrWhiteSpace(key))http.Headers.Authorization=new("Bearer",key);
        using var response=await client.SendAsync(http,ct);
        if(!response.IsSuccessStatusCode)throw new HttpRequestException(ConnectionTools.FriendlyError((int)response.StatusCode));
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        LastRaw=profile.NativeR1?doc.RootElement.GetProperty("content").GetString()??"":doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()??"";
        int first=LastRaw.IndexOf('{'),last=LastRaw.LastIndexOf('}');if(first<0||last<first)throw new InvalidDataException("模型未返回动作 JSON。请提高输出上限，或选择支持指令与 JSON 的模型。");
        PetReply reply;
        try{reply=JsonSerializer.Deserialize<PetReply>(LastRaw[first..(last+1)])??throw new InvalidDataException("模型回复为空");}catch(JsonException){throw new InvalidDataException("模型回复不完整。可在连接设置启用 JSON 模式，或提高输出上限。");}
        if(reply.frames is null||reply.frames.Count==0||reply.frames.Any(f=>f is null)||string.IsNullOrWhiteSpace(reply.say))throw new InvalidDataException("模型没有返回完整动作");
        reply.say=reply.say.Trim();if(reply.say.Length>65)reply.say=reply.say[..65]+"…";
        reply.frames=reply.frames.Take(3).ToList();foreach(var f in reply.frames)f.Normalize();
        if(config.MemoryTurns>0){memory.Add(interaction+" → "+reply.say);while(memory.Count>config.MemoryTurns)memory.RemoveAt(0);}
        return reply;
    }
}
public static class ConnectionTools {
    public static string FriendlyError(int status)=>status switch{401 or 403=>"API Key 无效或没有访问权限。",404=>"接口地址或模型 ID 不正确（404）。",429=>"服务限流或额度不足（429），请稍后重试。",>=500=>"模型服务暂时繁忙或尚未就绪。",_=>$"服务返回 HTTP {status}，请检查地址、模型及 JSON 模式。"};
    public static async Task<List<string>> Models(ConnectionProfile profile,string key,CancellationToken ct=default){
        if(!Settings.ValidEndpoint(profile.Endpoint))throw new InvalidDataException("请输入完整有效的服务地址。");
        using var c=new HttpClient{Timeout=TimeSpan.FromSeconds(8)};using var req=new HttpRequestMessage(HttpMethod.Get,profile.Endpoint.TrimEnd('/')+(profile.NativeR1?"/v1/models":"/models"));
        if(!string.IsNullOrWhiteSpace(key))req.Headers.Authorization=new("Bearer",key);
        using var r=await c.SendAsync(req,ct);if(!r.IsSuccessStatusCode)throw new HttpRequestException(FriendlyError((int)r.StatusCode));
        using var json=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));return json.RootElement.GetProperty("data").EnumerateArray().Select(v=>v.GetProperty("id").GetString()??"").Where(s=>s.Length>0).ToList();
    }
}

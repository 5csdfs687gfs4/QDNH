using System.Diagnostics;
using System.Text.Json;
Console.WriteLine("DOBLE DE PRUEBA: ningun acceso a radio ni audio");
string? heartbeat=Environment.GetEnvironmentVariable("QDNH_HEARTBEAT_FILE");
string? stopFile=Environment.GetEnvironmentVariable("QDNH_HOST_STOP_FILE");
string freezeFlag=Path.Combine(Environment.CurrentDirectory,"fixture-freeze-once.flag");
bool freeze=File.Exists(freezeFlag);
if(freeze) File.Delete(freezeFlag);
File.AppendAllText("fixture-starts.log",$"{Environment.ProcessId} freeze={freeze}\n");
var process=Process.GetCurrentProcess();
long started=process.StartTime.ToUniversalTime().Ticks;
long sequence=0;
void Publish()
{
    if(string.IsNullOrEmpty(heartbeat)) return;
    string temporary=heartbeat+".fixture.tmp";
    File.WriteAllText(temporary,JsonSerializer.Serialize(new { Protocol=1,ProcessId=Environment.ProcessId,ProcessStartTimeUtcTicks=started,Sequence=++sequence,State="Running" }));
    File.Move(temporary,heartbeat,true);
}
Publish();
if(freeze) { await Task.Delay(Timeout.Infinite); return; }
Task<string?> input=Console.In.ReadLineAsync();
while(true)
{
    if(input.IsCompleted)
    {
        if(await input=="Q") return;
        if(await input==null) Environment.Exit(13);
        input=Console.In.ReadLineAsync();
    }
    if(!string.IsNullOrEmpty(stopFile) && File.Exists(stopFile)) return;
    Publish();
    await Task.Delay(200);
}

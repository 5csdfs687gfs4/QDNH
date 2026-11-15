using System;
using System.Net;
using System.Net.Sockets;
using QDNH.Hosting;
using QDNH.Network;
using QDNH.SharedRadio;
using Xunit;
namespace QDNH.Tests;
public class ListenerReadinessTests
{
    [Fact]
    public void OccupiedPortCannotBeReportedReadyAndStoppedListenerLosesReadiness()
    {
        using var sessions=new SessionManager();
        var occupied=new TcpListener(IPAddress.Any,0); occupied.Start();
        int port=((IPEndPoint)occupied.LocalEndpoint).Port;
        ControlChannel? failed=null;
        try
        {
            Assert.Throws<SocketException>(()=>failed=new ControlChannel(port,sessions));
            Assert.False(new ListenerReadiness(false,false,false,false,true,failed?.IsListening==true).AllReady);
        }
        finally { occupied.Stop(); }
        var working=new ControlChannel(0,sessions);
        try
        {
            Assert.True(working.IsListening);
            Assert.True(new ListenerReadiness(false,false,false,false,true,working.IsListening).AllReady);
        }
        finally { working.Close(); }
        Assert.False(working.IsListening);
        Assert.False(new ListenerReadiness(false,false,false,false,true,working.IsListening).AllReady);
    }
    [Theory]
    [InlineData(true,false,true,true,true,true)]
    [InlineData(true,true,true,false,true,true)]
    [InlineData(true,true,true,true,true,false)]
    public void AnyRequiredMissingListenerDegradesStation(bool ae,bool a,bool se,bool s,bool ce,bool c)
        => Assert.False(new ListenerReadiness(ae,a,se,s,ce,c).AllReady);
}

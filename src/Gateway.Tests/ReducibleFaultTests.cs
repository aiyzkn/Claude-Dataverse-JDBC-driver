using System.ServiceModel;
using Gateway.Dataverse;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Gateway.Tests;

/// <summary>適応的バッチ縮小(F-12)のフォールト分類。</summary>
public class ReducibleFaultTests
{
    private static FaultException<OrganizationServiceFault> Fault(string message)
        => new(new OrganizationServiceFault { Message = message }, message);

    [Theory]
    [InlineData("The request channel timed out while waiting for a reply")]
    [InlineData("Operation timeout")]
    [InlineData("The message is too large")]
    [InlineData("Combined execution time of incoming requests exceeded limit")]
    public void Timeout_and_size_faults_are_reducible(string msg)
        => Assert.True(PreparedInsertExecutor.IsReducibleFault(Fault(msg)));

    [Theory]
    [InlineData("The attribute poc_foo was not found")]
    [InlineData("Principal user is missing prvCreate privilege")]
    [InlineData("Number of concurrent requests exceeded the limit of 52")]
    public void Other_faults_are_not_reducible(string msg)
        => Assert.False(PreparedInsertExecutor.IsReducibleFault(Fault(msg)));
}

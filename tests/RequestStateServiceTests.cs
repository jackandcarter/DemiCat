using System;
using System.Collections.Generic;
using DemiCatPlugin;
using Xunit;

public class RequestStateServiceTests
{
    [Fact]
    public void LoadAndPersistStates()
    {
        var config = new Config
        {
            RequestStates = new List<RequestState>
            {
                new RequestState { Id = "1", Title = "One", Status = RequestStatus.Open }
            },
            RequestsDeltaToken = "token1"
        };
        RequestStateService.Load(config);
        Assert.Single(RequestStateService.All);

        RequestStateService.Upsert(new RequestState { Id = "2", Title = "Two", Status = RequestStatus.Claimed });
        Assert.Equal(2, config.RequestStates.Count);

        RequestStateService.Remove("1");
        Assert.Single(RequestStateService.All);
        Assert.DoesNotContain(config.RequestStates, r => r.Id == "1");

        RequestStateService.Upsert(new RequestState
        {
            Id = "old",
            Title = "Old",
            Status = RequestStatus.Completed,
            CreatedAt = DateTime.UtcNow.AddDays(-30)
        });
        RequestStateService.Prune();
        Assert.DoesNotContain(RequestStateService.All, r => r.Id == "old");
    }
}

using ClinicQueue.Shared;

namespace ClinicQueue.Tests;

public class CoreTests
{
    [Fact]
    public void SeedDataContainsThreeDoctors()
    {
        Assert.Equal(3, ClinicSeedData.Doctors.Count);
    }

    [Theory]
    [InlineData("Available", ServiceStatus.Available)]
    [InlineData("busy", ServiceStatus.Busy)]
    [InlineData("OnBreak", ServiceStatus.OnBreak)]
    [InlineData("Closed", ServiceStatus.Closed)]
    public void ServiceStatusCanBeParsed(string value, ServiceStatus expected)
    {
        Assert.True(Enum.TryParse<ServiceStatus>(value, ignoreCase: true, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AppointmentRequestedEventKeepsCorrelationId()
    {
        var eventValue = new AppointmentRequestedEvent(Guid.NewGuid(), "correlation-123");

        Assert.Equal("correlation-123", eventValue.CorrelationId);
    }

    [Fact]
    public void QueueChangedEventKeepsRoomAndCorrelation()
    {
        var eventValue = new QueueChangedEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today),
            4,
            "Nguyen Van A",
            "Confirmed",
            "correlation-456");

        Assert.Equal(4, eventValue.QueueNumber);
        Assert.Equal("correlation-456", eventValue.CorrelationId);
    }
}

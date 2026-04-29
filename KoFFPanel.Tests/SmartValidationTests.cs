using KoFFPanel.Presentation.Features.Management;
using Xunit;
using Moq;
using KoFFPanel.Application.Interfaces;

namespace KoFFPanel.Tests;

public class SmartValidationTests
{
    [Fact]
    public void AddClientViewModel_ShouldValidateName_LatinOnly()
    {
        // Arrange
        var vm = new AddClientViewModel();
        vm.Initialize("1.1.1.1");
        
        // Act - Invalid name
        vm.ClientName = "Костя_123"; // Cyrillic
        vm.SaveCommand.Execute(null);
        
        // Assert
        Assert.False(vm.IsSuccess);
        Assert.Contains("Только латиница", vm.StatusMessage);
        
        // Act - Valid name
        vm.ClientName = "Kostya_123";
        vm.SaveCommand.Execute(null);
        
        // Assert
        Assert.True(vm.IsSuccess);
        Assert.Equal("", vm.StatusMessage);
    }

    [Fact]
    public void AddServerViewModel_ShouldValidateIpFormat()
    {
        // Arrange
        var mockRepo = new Mock<IProfileRepository>();
        var mockSsh = new Mock<ISshService>();
        var mockPicker = new Mock<IFilePickerService>();
        var vm = new AddServerViewModel(mockRepo.Object, mockSsh.Object, mockPicker.Object);
        
        // Act - Invalid IP
        vm.Name = "Test Server";
        vm.IpAddress = "192.168.1.500";
        vm.SaveCommand.Execute(null);
        
        // Assert
        Assert.Contains("Некорректный формат IP-адреса", vm.StatusMessage);
        mockRepo.Verify(r => r.AddProfile(It.IsAny<KoFFPanel.Domain.Entities.VpnProfile>()), Times.Never);
        
        // Act - Valid IP
        vm.IpAddress = "192.168.1.1";
        vm.SaveCommand.Execute(null);
        
        // Assert
        mockRepo.Verify(r => r.AddProfile(It.IsAny<KoFFPanel.Domain.Entities.VpnProfile>()), Times.Once);
    }

    [Fact]
    public void AddServerViewModel_ShouldValidatePortRange()
    {
        // Arrange
        var mockRepo = new Mock<IProfileRepository>();
        var mockSsh = new Mock<ISshService>();
        var mockPicker = new Mock<IFilePickerService>();
        var vm = new AddServerViewModel(mockRepo.Object, mockSsh.Object, mockPicker.Object);
        
        // Act - Invalid Port
        vm.Name = "Test Server";
        vm.IpAddress = "1.1.1.1";
        vm.Port = 70000;
        vm.SaveCommand.Execute(null);
        
        // Assert
        Assert.Contains("Порт должен быть от 1 до 65535", vm.StatusMessage);
    }
}
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Tests.Application.Auth;

public class RegisterWorkspaceUseCaseTests
{
    private static RegisterWorkspaceUseCase Build(
        FakeWorkspaceRepository? workspaces = null, FakeIdentityService? identity = null, FakeUnitOfWork? uow = null)
        => new(workspaces ?? new FakeWorkspaceRepository(), identity ?? new FakeIdentityService(),
            new FakeJwtTokenGenerator(), new FakeRefreshTokenService(), uow ?? new FakeUnitOfWork());

    [Fact]
    public async Task ExecuteAsync_CreatesWorkspaceAndReturnsTokens()
    {
        var workspaces = new FakeWorkspaceRepository();
        var uow = new FakeUnitOfWork();
        var sut = Build(workspaces, uow: uow);

        var result = await sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123!", "Alice"));

        Assert.NotNull(result.AccessToken);
        Assert.Equal("fake-refresh-token", result.RefreshToken);
        Assert.Single(workspaces.Created);
        Assert.True(uow.Committed);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateEmail_ThrowsAndCreatesNoWorkspace()
    {
        var workspaces = new FakeWorkspaceRepository();
        var identity = new FakeIdentityService { RegisteredEmails = { "a@acme.com" } };
        var sut = Build(workspaces, identity);

        await Assert.ThrowsAsync<EmailAlreadyRegisteredException>(
            () => sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123!", "Alice")));

        Assert.Empty(workspaces.Created);
    }

    [Fact]
    public async Task ExecuteAsync_UserCreationFails_RollsBackTransaction()
    {
        var uow = new FakeUnitOfWork();
        var identity = new FakeIdentityService { ExceptionOnCreate = new RegistrationFailedException(["bad password"]) };
        var sut = Build(identity: identity, uow: uow);

        await Assert.ThrowsAsync<RegistrationFailedException>(
            () => sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123!", "Alice")));

        Assert.True(uow.RolledBack);
        Assert.False(uow.Committed);
    }
}

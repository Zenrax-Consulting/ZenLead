namespace ZenLead.Application.Abstractions;

public class EmailAlreadyRegisteredException() : InvalidOperationException("Email already registered.");

public class RegistrationFailedException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

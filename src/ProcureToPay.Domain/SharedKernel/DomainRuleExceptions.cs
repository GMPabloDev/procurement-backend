namespace ProcureToPay.Domain.SharedKernel;

public sealed class DomainValidationException(string message) : DomainException(message)
{
}

public sealed class DomainConflictException(string message) : DomainException(message)
{
}

public sealed class DomainForbiddenException(string message) : DomainException(message)
{
}

public sealed class DomainNotFoundException(string message) : DomainException(message)
{
}

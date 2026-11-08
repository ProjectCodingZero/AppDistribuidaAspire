namespace AppDistribuidaAspire.Domain;

public class EntityBase
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
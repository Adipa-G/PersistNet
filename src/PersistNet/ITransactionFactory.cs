using System.Threading.Tasks;

namespace PersistNet;

public interface ITransactionFactory
{
    Task<ITransaction> OpenTransactionAsync();
}       
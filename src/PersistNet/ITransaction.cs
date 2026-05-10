using System;
using System.Threading.Tasks;

namespace PersistNet;

public interface ITransaction : IAsyncDisposable
{
    void Save<T>(T entity);
    Task<T> SaveAndCommitAsync<T>(T entity);
    void Delete<T>(T entity);
    Task DeleteAndCommitAsync<T>(T entity);
    Task<T> GetAsync<T>(params object[] keyValues) where T : class;
    Task CommitAsync();
}
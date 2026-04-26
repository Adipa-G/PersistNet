using System;
using System.Threading.Tasks;

namespace PersistNet;

public interface ITransaction : IAsyncDisposable
{
    void Save<T>(T entity);
    Task<T> SaveAndFlushAsync<T>(T entity);
    void Delete<T>(T entity);
    Task DeleteAndFlushAsync<T>(T entity);
    Task<T> GetAsync<T>(object id);
    Task CommitAsync();
}
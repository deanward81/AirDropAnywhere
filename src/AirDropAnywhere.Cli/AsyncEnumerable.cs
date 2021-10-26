using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AirDropAnywhere.Cli
{
    internal static class AsyncEnumerable
    {
        /// <summary>
        /// Returns the first element of an <see cref="IAsyncEnumerable{T}"/>.
        /// </summary>
        /// <param name="source">
        /// <see cref="IAsyncEnumerable{T}"/> to enumerate.
        /// </param>
        /// <param name="cancellationToken">
        /// <see cref="CancellationToken"/> used to cancel the operation
        /// </param>
        /// <typeparam name="T">
        /// Type of element in the <see cref="IAsyncEnumerable{T}"/>
        /// </typeparam>
        /// <returns>
        /// First element or <c>default</c> if none was found.
        /// </returns>
        public static async ValueTask<T?> FirstOrDefaultAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken)
        {
            await using var e = source.GetAsyncEnumerator(cancellationToken);
            if (await e.MoveNextAsync())
            {
                return e.Current;
            }

            return default;
        }
        
    }
}
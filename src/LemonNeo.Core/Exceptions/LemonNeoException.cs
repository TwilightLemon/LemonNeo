namespace LemonNeo.Core.Exceptions;

public abstract class LemonNeoException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LibraryException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);

public sealed class ProviderException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);

public sealed class PlaybackResolutionException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);

public sealed class SyncException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);
using System;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LanguageExt.Common;

namespace LanguageExt.Thunks
{
    /// <summary>
    /// Lazily evaluates an asynchronous function and then memoizes the value
    /// Runs at most once
    /// </summary>
    public class ThunkAsync<A>
    {
        internal readonly Func<ValueTask<Fin<A>>> fun;
        internal volatile int state;
        internal Error error;
        internal A value;

        /// <summary>
        /// Construct a lazy thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Lazy(Func<ValueTask<Fin<A>>> fun) =>
            new ThunkAsync<A>(fun);

        /// <summary>
        /// Construct a lazy ThunkAsync
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Lazy(Func<ValueTask<A>> fun) =>
            new ThunkAsync<A>(async () => Fin<A>.Succ(await fun()));

        /// <summary>
        /// Construct an error ThunkAsync
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Fail(Error error) =>
            new ThunkAsync<A>(Thunk.IsFailed, error);

        /// <summary>
        /// Construct a success thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Success(A value) =>
            new ThunkAsync<A>(value);

        /// <summary>
        /// Construct a cancelled thunk
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public static ThunkAsync<A> Cancelled() =>
            new ThunkAsync<A>(Thunk.IsCancelled, Error.New(Thunk.CancelledText));

        /// <summary>
        /// Success ctor
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        ThunkAsync(A value) =>
            (this.state, this.value) = (Thunk.IsSuccess, value);

        /// <summary>
        /// Failed / Cancelled constructor
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        ThunkAsync(int state, Error error) =>
            (this.state, this.error) = (state, error);

        /// <summary>
        /// Lazy constructor
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        ThunkAsync(Func<ValueTask<Fin<A>>> fun) =>
            this.fun = fun ?? throw new ArgumentNullException(nameof(value));

        /// <summary>
        /// Value accessor
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public ValueTask<Fin<A>> Value() =>
            Eval();

        [MethodImpl(Thunk.mops)]
        public Unit Flush()
        {
            if (fun == null)
            {
                return default;
            }
            else
            {
                SpinIfEvaluating();
                state = Thunk.NotEvaluated;
                return default;
            }
        }

        /// <summary>
        /// Functor map
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        public ThunkAsync<B> Map<B>(Func<A, B> f)
        {
            try
            {
                while (true)
                {
                    SpinIfEvaluating();

                    switch (state)
                    {
                        case Thunk.IsSuccess:
                            return ThunkAsync<B>.Success(f(value));

                        case Thunk.NotEvaluated:
                            return ThunkAsync<B>.Lazy(async () =>
                            {
                                var ev = await fun();
                                if (ev.IsFail)
                                {
                                    return ev.Cast<B>();
                                }
                                else
                                {
                                    return Fin<B>.Succ(f(ev.data.Right));
                                }
                            });

                        case Thunk.IsCancelled:
                            return ThunkAsync<B>.Cancelled();

                        case Thunk.IsFailed:
                            return ThunkAsync<B>.Fail(error);
                    }
                }
            }
            catch (Exception e)
            {
                return ThunkAsync<B>.Fail(e);
            }
        }
        
        /// <summary>
        /// Functor map
        /// </summary>
        [Pure]
        public ThunkAsync<B> MapAsync<B>(Func<A, ValueTask<B>> f)
        {
            try
            {
                while (true)
                {
                    SpinIfEvaluating();

                    switch (state)
                    {
                        case Thunk.IsSuccess:
                            return ThunkAsync<B>.Lazy(async () => await f(value));

                        case Thunk.NotEvaluated:
                            return ThunkAsync<B>.Lazy(async () =>
                            {
                                var ev = await fun();
                                if (ev.IsFail)
                                {
                                    return ev.Cast<B>();
                                }
                                else
                                {
                                    return Fin<B>.Succ(await f(ev.data.Right));
                                }
                            });

                        case Thunk.IsCancelled:
                            return ThunkAsync<B>.Cancelled();

                        case Thunk.IsFailed:
                            return ThunkAsync<B>.Fail(error);
                    }
                }
            }
            catch (Exception e)
            {
                return ThunkAsync<B>.Fail(e);
            }
        }

        /// <summary>
        /// Evaluates the lazy function if necessary, returns the result if it's previously been evaluated
        /// The thread goes into a spin-loop if more than one thread tries to access the lazy value at once.
        /// This is to protect against multiple evaluations.  This technique allows for a lock free access
        /// the vast majority of the time.
        /// </summary>
        [Pure, MethodImpl(Thunk.mops)]
        async ValueTask<Fin<A>> Eval()
        {
            while (true)
            {
                if (Interlocked.CompareExchange(ref state, Thunk.Evaluating, Thunk.NotEvaluated) == Thunk.NotEvaluated)
                {
                    try
                    {
                        var vt = fun();
                        var ex = await vt;
                        if (vt.CompletedSuccessfully())
                        {
                            if (ex.IsFail)
                            {
                                error = ex.Error;
                                state = Thunk.IsFailed; // state update must be last thing before return
                                return ex;
                            }
                            else
                            {
                                value = ex.Value;
                                state = Thunk.IsSuccess; // state update must be last thing before return
                                return ex;
                            }
                        }
                        else if (vt.IsCanceled)
                        {
                            error = Error.New(Thunk.CancelledText);
                            state = Thunk.IsCancelled; // state update must be last thing before return
                            return Fin<A>.Fail(error);
                        }
                        else
                        {
                            var e = vt.AsTask().Exception;
                            error = e;
                            state = Thunk.IsFailed; // state update must be last thing before return
                            return Fin<A>.Fail(error);
                        }
                    }
                    catch (Exception e)
                    {
                        error = e;
                        state = e.Message == Thunk.CancelledText // state update must be last thing before return
                            ? Thunk.IsCancelled
                            : Thunk.IsFailed; 
                        return Fin<A>.Fail(Error.New(e));
                    }
                }
                else
                {
                    SpinIfEvaluating();

                    // Once we're here we should have a result from the eval thread and
                    // so we can use `value` to return
                    switch (state)
                    {
                        case Thunk.NotEvaluated: 
                        case Thunk.Evaluating: 
                            continue;
                        case Thunk.IsSuccess: 
                            return Fin<A>.Succ(value);
                        case Thunk.IsCancelled: 
                            return Fin<A>.Fail(Error.New(Thunk.CancelledText));
                        case Thunk.IsFailed:
                            return Fin<A>.Fail(Error.New(error));
                        default:
                            throw new InvalidOperationException("should never happen");
                    }
                }
            }
        }
        
        /// <summary>
        /// Spin whilst it's running so we don't run the operation twice
        /// this will block obviously, but this event should be super rare
        /// and it's purely to stop race conditions with the eval
        /// </summary>
        [MethodImpl(Thunk.mops)]
        public void SpinIfEvaluating()
        {
            while (state == Thunk.Evaluating)
            {
                SpinWait sw = default;
                sw.SpinOnce();
            }
        }
 
        [Pure, MethodImpl(Thunk.mops)]
        public override string ToString() =>
            state switch
            {
                Thunk.NotEvaluated => "Not evaluated",
                Thunk.Evaluating => "Evaluating",
                Thunk.IsSuccess => $"Success({value})",
                Thunk.IsCancelled => $"Cancelled",
                Thunk.IsFailed => $"Failed({error})",
                _ => ""
            };
    }
}
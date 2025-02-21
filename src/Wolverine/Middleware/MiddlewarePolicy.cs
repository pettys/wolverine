using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Middleware;

public class MiddlewarePolicy : IChainPolicy
{
    public static readonly string[] BeforeMethodNames = { "Before", "BeforeAsync", "Load", "LoadAsync" };
    public static readonly string[] AfterMethodNames = { "After", "AfterAsync", "PostProcess", "PostProcessAsync" };
    public static readonly string[] FinallyMethodNames = { "Finally", "FinallyAsync" };

    private readonly List<Application> _applications = new();

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
    {
        var applications = _applications;

        foreach (var chain in chains) ApplyToChain(applications, rules, chain);
    }


    public static void AssertMethodDoesNotHaveDuplicateReturnValues(MethodCall call)
    {
        if (call.Method.ReturnType.IsValueType)
        {
            var duplicates = call.Method.ReturnType.GetGenericArguments().GroupBy(x => x).ToArray();
            if (duplicates.Any(x => x.Count() > 1))
            {
                throw new InvalidWolverineMiddlewareException(
                    $"Wolverine middleware cannot support multiple 'creates' variables of the same type. Method {call}, arguments {duplicates.Select(x => x.Key.Name).Join(", ")}");
            }
        }
    }

    internal static void ApplyToChain(List<Application> applications, GenerationRules rules, IChain chain)
    {
        var befores = applications.SelectMany(x => x.BuildBeforeCalls(chain, rules)).ToArray();

        if (chain.InputType() != null &&
            befores.SelectMany(x => x.Creates).Any(x => x.VariableType == chain.InputType()))
        {
            throw new InvalidWolverineMiddlewareException(
                "It's not currently legal in Wolverine to return the message type from middleware");
        }

        for (var i = 0; i < befores.Length; i++)
        {
            chain.Middleware.Insert(i, befores[i]);
        }

        var afters = applications.ToArray().Reverse().SelectMany(x => x.BuildAfterCalls(chain, rules));

        chain.Postprocessors.AddRange(afters);
    }


    public Application AddType(Type middlewareType, Func<IChain, bool>? filter = null)
    {
        filter ??= _ => true;
        var application = new Application(middlewareType, filter);
        _applications.Add(application);
        return application;
    }

    public static IEnumerable<MethodInfo> FilterMethods<T>(IEnumerable<MethodInfo> methods, string[] validNames)
        where T : Attribute
    {
        return methods
            .Where(x => !x.HasAttribute<WolverineIgnoreAttribute>())
            .Where(x => validNames.Contains(x.Name) || x.HasAttribute<T>());
    }

    public class Application
    {
        private readonly MethodInfo[] _afters;
        private readonly MethodInfo[] _befores;
        private readonly ConstructorInfo? _constructor;
        private readonly MethodInfo[] _finals;

        public Application(Type middlewareType, Func<IChain, bool> filter)
        {
            if (!middlewareType.IsPublic && !middlewareType.IsVisible)
            {
                throw new InvalidWolverineMiddlewareException(middlewareType);
            }

            if (!middlewareType.IsStatic())
            {
                var constructors = middlewareType.GetConstructors();
                if (constructors.Length != 1)
                {
                    throw new InvalidWolverineMiddlewareException(middlewareType);
                }

                _constructor = constructors.Single();
            }

            MiddlewareType = middlewareType;
            Filter = filter;

            var methods = middlewareType.GetMethods().ToArray();

            _befores = FilterMethods<WolverineBeforeAttribute>(methods, BeforeMethodNames).ToArray();
            _afters = FilterMethods<WolverineAfterAttribute>(methods, AfterMethodNames).ToArray();
            _finals = FilterMethods<WolverineFinallyAttribute>(methods, FinallyMethodNames).ToArray();

            if (!_befores.Any() && !_afters.Any() && !_finals.Any())
            {
                throw new InvalidWolverineMiddlewareException(middlewareType);
            }
        }

        public Type MiddlewareType { get; }
        public Func<IChain, bool> Filter { get; }


        public bool MatchByMessageType { get; set; }

        public IEnumerable<Frame> BuildBeforeCalls(IChain chain, GenerationRules rules)
        {
            var frames = buildBefores(chain, rules).ToArray();
            if (frames.Any() && !MiddlewareType.IsStatic())
            {
                var constructorFrame = new ConstructorFrame(MiddlewareType, _constructor!);
                if (MiddlewareType.CanBeCastTo<IDisposable>() || MiddlewareType.CanBeCastTo<IAsyncDisposable>())
                {
                    constructorFrame.Mode = ConstructorCallMode.UsingNestedVariable;
                }

                yield return constructorFrame;
            }

            foreach (var frame in frames) yield return frame;
        }

        private IEnumerable<Frame> wrapBeforeFrame(MethodCall call, GenerationRules rules)
        {
            if (_finals.Any())
            {
                if (rules.TryFindContinuationHandler(call, out var frame))
                {
                    call.Next = frame;
                }

                Frame[] finals = _finals.Select(x => new MethodCall(MiddlewareType, x)).OfType<Frame>().ToArray();

                yield return new TryFinallyWrapperFrame(call, finals);
            }
            else
            {
                yield return call;
                if (rules.TryFindContinuationHandler(call, out var frame))
                {
                    yield return frame!;
                }
            }
        }

        private MethodCall? buildCallForBefore(IChain chain, MethodInfo before)
        {
            if (MatchByMessageType)
            {
                var inputType = chain.InputType();
                var messageType = before.MessageType();

                if (messageType != null && inputType.CanBeCastTo(messageType))
                {
                    return new MethodCallAgainstMessage(MiddlewareType, before, inputType!);
                }
            }
            else
            {
                return new MethodCall(MiddlewareType, before);
            }

            return null;
        }

        private IEnumerable<Frame> buildBefores(IChain chain, GenerationRules rules)
        {
            if (!Filter(chain))
            {
                yield break;
            }

            if (!_befores.Any() && _finals.Any())
            {
                var finals = buildFinals(chain).ToArray();

                yield return new TryFinallyWrapperFrame(new CommentFrame("Wrapped by middleware"), finals);
                yield break;
            }

            foreach (var before in _befores)
            {
                var call = buildCallForBefore(chain, before);
                if (call != null)
                {
                    AssertMethodDoesNotHaveDuplicateReturnValues(call);

                    foreach (var frame in wrapBeforeFrame(call, rules)) yield return frame;
                }
            }
        }


        private IEnumerable<Frame> buildFinals(IChain chain)
        {
            foreach (var final in _finals)
            {
                if (MatchByMessageType)
                {
                    var messageType = final.MessageType();
                    if (messageType != null && chain.InputType().CanBeCastTo(messageType))
                    {
                        yield return new MethodCallAgainstMessage(MiddlewareType, final, chain.InputType()!);
                    }
                }
                else
                {
                    yield return new MethodCall(MiddlewareType, final);
                }
            }
        }

        public IEnumerable<Frame> BuildAfterCalls(IChain chain, GenerationRules rules)
        {
            var afters = buildAfters(chain).ToArray();

            if (afters.Any() && !MiddlewareType.IsStatic())
            {
                if (chain.Middleware.OfType<ConstructorFrame>().All(x => x.Variable.VariableType != MiddlewareType))
                {
                    yield return new ConstructorFrame(MiddlewareType, _constructor!);
                }
            }

            foreach (var after in afters) yield return after;
        }

        private IEnumerable<Frame> buildAfters(IChain chain)
        {
            if (!Filter(chain))
            {
                yield break;
            }

            foreach (var after in _afters)
            {
                if (MatchByMessageType)
                {
                    var messageType = after.MessageType();
                    if (messageType != null && chain.InputType().CanBeCastTo(messageType))
                    {
                        yield return new MethodCallAgainstMessage(MiddlewareType, after, chain.InputType()!);
                    }
                }
                else
                {
                    yield return new MethodCall(MiddlewareType, after);
                }
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Antlr4.Runtime;
using Plang.Compiler.TypeChecker.AST.Declarations;
using Plang.Compiler.TypeChecker.AST.States;
using Plang.Compiler.TypeChecker.Types;

namespace Plang.Compiler.TypeChecker
{
    public static class MachineChecker
    {
        public static void Validate(ITranslationErrorHandler handler, Machine machine, ICompilerConfiguration job, Scope gScope)
        {
            // before validating the machines, lets set the constructor types for machines and interfaces
            if(!machine.IsSpec) InitializeContructorType(handler, machine, gScope);

            ValidateHandlers(handler, machine);
            ValidateTransitions(handler, machine);
            // special validation for monitors:
            // ensure that each event handler is in the observe set.
            ValidateSpecObservesList(handler, machine, job);
        }

        private static void InitializeContructorType(ITranslationErrorHandler handler, Machine machine, Scope gScope)
        {
            var startState = FindStartState(machine, handler);
            machine.PayloadType = GetStatePayload(startState);
            gScope.Get(machine.Name, out Interface @interface);
            @interface.PayloadType = machine.PayloadType;
        }

        private static void ValidateSpecObservesList(ITranslationErrorHandler handler, Machine machine, ICompilerConfiguration job)
        {
            if (machine.IsSpec)
            {
                foreach (var state in machine.AllStates())
                {
                    foreach (var pair in state.AllEventHandlers)
                    {
                        if (!machine.Observes.Events.Contains(pair.Key))
                        {
                            job.Output.WriteWarning(
                                handler.SpecObservesSetIncompleteWarning(pair.Value.SourceLocation, pair.Key, machine));
                        }
                    }
                }
            }
        }

        private static void ValidateHandlers(ITranslationErrorHandler handler, Machine machine)
        {
            foreach (var state in machine.AllStates())
            {
                if (state.Entry?.Signature.Parameters.Count > 1)
                {
                    throw handler.MoreThanOneParameterForHandlers(state.SourceLocation, state.Entry.Signature.Parameters.Count);
                }

                if (state.Exit?.Signature.Parameters.Count > 0)
                {
                    throw handler.ExitFunctionCannotTakeParameters(state.SourceLocation, state.Exit.Signature.Parameters.Count);
                }

                foreach (var pair in state.AllEventHandlers)
                {
                    var handledEvent = pair.Key;
                    switch (pair.Value)
                    {
                        case EventDoAction eventDoAction:
                            if (eventDoAction.Target != null && eventDoAction.Target.Signature.ParameterTypes.Count() > 1)
                            {
                                throw handler.MoreThanOneParameterForHandlers(eventDoAction.SourceLocation,
                                    eventDoAction.Target.Signature.ParameterTypes.Count());
                            }
                            break;

                        case EventGotoState eventGotoState:
                            if (eventGotoState.TransitionFunction != null && eventGotoState.TransitionFunction.Signature.ParameterTypes.Count() > 1)
                            {
                                throw handler.MoreThanOneParameterForHandlers(eventGotoState.SourceLocation,
                                    eventGotoState.TransitionFunction.Signature.ParameterTypes.Count());
                            }
                            break;
                        case EventDefer _:
                        case EventIgnore _:
                        {
                            break;
                        }
                    }
                }
            }
        }

        public static void ValidateNoStaticHandlers(ITranslationErrorHandler handler, Machine machine)
        {
            foreach (var state in machine.AllStates())
            {
                var illegalUsage = state.Entry != null && IsStaticOrForeign(state.Entry);
                if (illegalUsage)
                {
                    throw handler.StaticFunctionNotAllowedAsHandler(state.SourceLocation,
                        state.Entry.Name);
                }

                illegalUsage = state.Exit != null && IsStaticOrForeign(state.Exit);
                if (illegalUsage)
                {
                    throw handler.StaticFunctionNotAllowedAsHandler(state.SourceLocation,
                        state.Exit.Name);
                }

                foreach (var pair in state.AllEventHandlers)
                {
                    switch (pair.Value)
                    {
                        case EventDoAction eventDoAction:
                            if (eventDoAction.Target != null && IsStaticOrForeign(eventDoAction.Target))
                            {
                                throw handler.StaticFunctionNotAllowedAsHandler(eventDoAction.SourceLocation,
                                    eventDoAction.Target.Name);
                            }

                            break;

                        case EventGotoState eventGotoState:
                            if (eventGotoState.TransitionFunction != null &&
                                IsStaticOrForeign(eventGotoState.TransitionFunction))
                            {
                                throw handler.StaticFunctionNotAllowedAsHandler(eventGotoState.SourceLocation,
                                    eventGotoState.TransitionFunction.Name);
                            }

                            break;

                        case EventDefer _:
                        case EventIgnore _:
                            break;

                        default:
                            throw handler.InternalError(pair.Value.SourceLocation,
                                new Exception("Unknown transition type parsed, report to the P team"));
                    }
                }
            }
        }

        private static bool IsStaticOrForeign(Function function)
        {
            return function.Owner == null || function.IsForeign;
        }

        private static void ValidateTransitions(ITranslationErrorHandler handler, Machine machine)
        {
            foreach (var state in machine.AllStates())
            {
                foreach (var pair in state.AllEventHandlers)
                {
                    var handledEvent = pair.Key;
                    switch (pair.Value)
                    {
                        case EventDoAction eventDoAction:
                            if (eventDoAction.Target != null)
                            {
                                ValidateEventPayloadToTransitionTarget(handler: handler, sourceLocation: eventDoAction.SourceLocation,
                                    eventPayloadType: handledEvent.PayloadType, targetFunction: eventDoAction.Target);
                            }

                            break;

                        case EventGotoState eventGotoState:
                            if (eventGotoState.Target.Entry != null)
                            {
                                ValidateEventPayloadToTransitionTarget(handler: handler, sourceLocation: eventGotoState.SourceLocation,
                                    eventPayloadType: handledEvent.PayloadType, targetFunction: eventGotoState.Target.Entry);
                            }

                            if (eventGotoState.TransitionFunction != null)
                            {
                                ValidateEventPayloadToTransitionTarget(handler: handler, sourceLocation: eventGotoState.SourceLocation,
                                    eventPayloadType: handledEvent.PayloadType, targetFunction: eventGotoState.TransitionFunction);
                            }

                            break;

                        case EventDefer _:
                        case EventIgnore _:
                        {
                            break;
                        }
                    }
                }
            }
        }

        private static void ValidateEventPayloadToTransitionTarget(ITranslationErrorHandler handler,
            ParserRuleContext sourceLocation,
            PLanguageType eventPayloadType,
            Function targetFunction)
        {
            IReadOnlyList<PLanguageType> entrySignature = targetFunction.Signature.ParameterTypes.ToList();
            if (entrySignature.Count == 0)
            {
                return;
            }

            if (entrySignature.Count > 1)
            {
                throw handler.InternalError(sourceLocation, new Exception("Target function cannot have multiple parameters (report this to the P developers)"));
            }

            if (entrySignature.Count == 1 && entrySignature[0].IsAssignableFrom(eventPayloadType))
            {
                return;
            }

            if (entrySignature.Count == 1 && eventPayloadType.Canonicalize() is TupleType tuple &&
                tuple.Types.Count == 1 && entrySignature[0].IsAssignableFrom(tuple.Types[0]))
            {
                return;
            }

            if (entrySignature.Count == 1)
            {
                throw handler.TypeMismatch(sourceLocation, eventPayloadType, entrySignature[0]);
            }

            PLanguageType entrySignatureType = new TupleType(entrySignature.ToArray());
            if (!entrySignatureType.IsAssignableFrom(eventPayloadType))
            {
                throw handler.TypeMismatch(sourceLocation, eventPayloadType, entrySignatureType);
            }
        }

        private static PLanguageType GetStatePayload(State startState)
        {
            return startState.Entry?.Signature.Parameters.ElementAtOrDefault(0)?.Type ?? PrimitiveType.Null;
        }

        private static State FindStartState(Machine machine, ITranslationErrorHandler handler)
        {
            var foundStartState = false;
            foreach (var state in machine.AllStates())
            {
                if (state == machine.StartState || state.IsStart)
                {
                    if (!foundStartState)
                    {
                        foundStartState = true;
                    }
                    else
                    {
                        throw handler.TwoStartStates(machine, state);
                    }
                }
            }

            Debug.Assert(!(foundStartState && machine.StartState == null), "machine has unregistered start state");

            if (!foundStartState || machine.StartState == null)
            {
                throw handler.MissingStartState(machine);
            }

            return machine.StartState;
        }
    }
}
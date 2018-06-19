﻿using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Microsoft.Pc.TypeChecker.AST;
using Microsoft.Pc.TypeChecker.AST.Declarations;
using Microsoft.Pc.TypeChecker.AST.ModuleExprs;

namespace Microsoft.Pc.TypeChecker
{
    internal static class ModuleSystemDeclarations
    {
        public static void PopulateAllModuleExprs(
            ITranslationErrorHandler handler,
            Scope globalScope)
        {
            var modExprVisitor = new ModuleExprVisitor(handler, globalScope);
           
            // first do all the named modules
            foreach (NamedModule mod in globalScope.NamedModules)
            {
                var context = (PParser.NamedModuleDeclContext) mod.SourceLocation;
                mod.ModExpr = modExprVisitor.Visit(context.modExpr());
            }

            // all the test declarations
            foreach (SafetyTest test in globalScope.SafetyTests)
            {
                var context = (PParser.SafetyTestDeclContext) test.SourceLocation;
                test.ModExpr = modExprVisitor.Visit(context.modExpr());
            }

            foreach (RefinementTest test in globalScope.RefinementTests)
            {
                var context = (PParser.RefinementTestDeclContext) test.SourceLocation;
                test.LeftModExpr = modExprVisitor.Visit(context.modExpr()[0]);
                test.RightModExpr = modExprVisitor.Visit(context.modExpr()[1]);
            }

            if (globalScope.Implementations.Any())
            {
                // all user defind implementations
                foreach (Implementation impl in globalScope.Implementations)
                {
                    var context = (PParser.ImplementationDeclContext)impl.SourceLocation;
                    impl.ModExpr = modExprVisitor.Visit(context.modExpr());
                }
            }
            else
            {
                var defaultImplDecl = new Implementation(ParserRuleContext.EmptyContext, "DefaultImpl");
                // create bindings from each machine to itself
                var defaultBindings = new List<Tuple<Interface, Machine>>();
                foreach (var machine in globalScope.Machines)
                {
                    globalScope.Get(machine.Name, out Interface @interface);
                    defaultBindings.Add(new Tuple<Interface, Machine>(@interface, machine));
                }
                defaultImplDecl.ModExpr = new BindModuleExpr(ParserRuleContext.EmptyContext, defaultBindings);

                globalScope.AddDefaultImpl(defaultImplDecl);
            }
                
        }
    }

    internal class ModuleExprVisitor : PParserBaseVisitor<IPModuleExpr>
    {
        private readonly Scope globalScope;
        private readonly ITranslationErrorHandler handler;

        public ModuleExprVisitor(
            ITranslationErrorHandler handler,
            Scope globalScope)
        {
            this.handler = handler;
            this.globalScope = globalScope;
        }

        public override IPModuleExpr VisitNamedModule([NotNull] PParser.NamedModuleContext context)
        {
            // check if the named module is declared
            if (!globalScope.Get(context.GetText(), out NamedModule mod))
            {
                throw handler.MissingDeclaration(context, "module", context.GetText());
            }

            var declContext = (PParser.NamedModuleDeclContext) mod.SourceLocation;
            return Visit(declContext.modExpr());
        }

        public override IPModuleExpr VisitPrimitiveModuleExpr([NotNull] PParser.PrimitiveModuleExprContext context)
        {
            var bindings = context._bindslist.Select(VisitBindExpr).ToList();
            return new BindModuleExpr(context, bindings);
        }

        public override IPModuleExpr VisitComposeModuleExpr([NotNull] PParser.ComposeModuleExprContext context)
        {
            var moduleList = context._mexprs.Select(Visit).ToList();
            return new UnionOrComposeModuleExpr(context, moduleList, true);
        }

        public override IPModuleExpr VisitHideEventsModuleExpr([NotNull] PParser.HideEventsModuleExprContext context)
        {
            var eventList = new List<PEvent>();
            foreach (PParser.NonDefaultEventContext eventName in context.nonDefaultEventList()._events)
            {
                if (!globalScope.Get(eventName.GetText(), out PEvent @event))
                {
                    throw handler.MissingDeclaration(eventName, "event", eventName.GetText());
                }

                eventList.Add(@event);
            }

            return new HideEventModuleExpr(context, eventList, Visit(context.modExpr()));
        }

        public override IPModuleExpr VisitHideInterfacesModuleExpr(
            [NotNull] PParser.HideInterfacesModuleExprContext context)
        {
            var interfaceList = new List<Interface>();
            foreach (PParser.IdenContext interfaceName in context.idenList()._names)
            {
                if (!globalScope.Get(interfaceName.GetText(), out Interface @interface))
                {
                    throw handler.MissingDeclaration(interfaceName, "interface", interfaceName.GetText());
                }

                interfaceList.Add(@interface);
            }

            return new HideInterfaceModuleExpr(context, interfaceList, Visit(context.modExpr()));
        }

        public override IPModuleExpr VisitRenameModuleExpr([NotNull] PParser.RenameModuleExprContext context)
        {
            if (!globalScope.Get(context.newName.GetText(), out Interface newInterface))
            {
                throw handler.MissingDeclaration(context.newName, "interface", context.newName.GetText());
            }

            if (!globalScope.Get(context.oldName.GetText(), out Interface oldInterface))
            {
                throw handler.MissingDeclaration(context.oldName, "interface", context.oldName.GetText());
            }

            return new RenameModuleExpr(context, newInterface, oldInterface, Visit(context.modExpr()));
        }

        public override IPModuleExpr VisitUnionModuleExpr([NotNull] PParser.UnionModuleExprContext context)
        {
            var moduleList = context._mexprs.Select(Visit).ToList();
            return new UnionOrComposeModuleExpr(context, moduleList, false);
        }

        public override IPModuleExpr VisitAssertModuleExpr([NotNull] PParser.AssertModuleExprContext context)
        {
            var monList = new List<Machine>();
            foreach (PParser.IdenContext monName in context.idenList()._names)
            {
                if (!globalScope.Get(monName.GetText(), out Machine mon))
                {
                    throw handler.MissingDeclaration(monName, "spec machine", monName.GetText());
                }

                if (!mon.IsSpec)
                {
                    handler.IssueError(monName, $"expected a specification machine instead of {mon.Name}");
                }
                else
                {
                    monList.Add(mon);
                }
            }

            return new AssertModuleExpr(context, monList, Visit(context.modExpr()));
        }

        private new Tuple<Interface, Machine> VisitBindExpr([NotNull] PParser.BindExprContext context)
        {
            string machine = context.mName.GetText();
            string @interface = context.iName?.GetText() ?? machine;

            if (!globalScope.Get(@interface, out Interface i))
            {
                throw handler.MissingDeclaration(context.iName, "interface", @interface);
            }

            if (!globalScope.Get(machine, out Machine m))
            {
                throw handler.MissingDeclaration(context.mName, "machine", machine);
            }

            return Tuple.Create(i, m);
        }
    }
}

﻿using Microsoft.CodeAnalysis.Semantics;
using Pchp.Syntax;
using Roslyn.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AST = Pchp.Syntax.AST;

namespace Pchp.CodeAnalysis.Semantics
{
    /// <summary>
    /// Binds syntax nodes (<see cref="AST.LangElement"/>) to semantic nodes (<see cref="IOperation"/>).
    /// </summary>
    internal class SemanticsBinder
    {
        #region Construction

        public SemanticsBinder(/*PhpCompilation compilation, AST.GlobalCode ast, bool ignoreAccessibility*/)
        {
        }

        #endregion

        #region Helpers

        public IEnumerable<BoundStatement> BindStatements(IEnumerable<AST.Statement> statements)
        {
            return statements.Select(BindStatement);
        }

        public ImmutableArray<BoundExpression> BindExpressions(IEnumerable<AST.Expression> expressions)
        {
            return expressions.Select(BindExpression).ToImmutableArray();
        }

        BoundExpression BindExpression(AST.Expression expr) => BindExpression(expr, BoundAccess.Read);

        ImmutableArray<BoundArgument> BindArguments(IEnumerable<AST.Expression> expressions)
        {
            return BindExpressions(expressions)
                .Select(x => new BoundArgument(x))
                .ToImmutableArray();
        }

        ImmutableArray<BoundArgument> BindArguments(IEnumerable<AST.ActualParam> parameters)
        {
            if (parameters.Any(p => p.IsVariadic || p.Ampersand))
                throw new NotImplementedException();

            return BindExpressions(parameters.Select(p => p.Expression))
                .Select(x => new BoundArgument(x))
                .ToImmutableArray();
        }

        #endregion

        public BoundStatement BindStatement(AST.Statement stmt)
        {
            Debug.Assert(stmt != null);

            if (stmt is AST.EchoStmt) return new BoundExpressionStatement(new BoundEcho(BindArguments(((AST.EchoStmt)stmt).Parameters)));
            if (stmt is AST.ExpressionStmt) return new BoundExpressionStatement(BindExpression(((AST.ExpressionStmt)stmt).Expression, BoundAccess.None));
            if (stmt is AST.JumpStmt) return BindJumpStmt((AST.JumpStmt)stmt);

            throw new NotImplementedException(stmt.GetType().FullName);
        }

        BoundStatement BindJumpStmt(AST.JumpStmt stmt)
        {
            if (stmt.Type == AST.JumpStmt.Types.Return)
            {
                return new BoundReturnStatement(
                    (stmt.Expression != null)
                        ? BindExpression(stmt.Expression, BoundAccess.Read)   // ReadRef in case routine returns an aliased value
                        : null);
            }

            throw ExceptionUtilities.Unreachable;
        }

        public BoundExpression BindExpression(AST.Expression expr, BoundAccess access)
        {
            Debug.Assert(expr != null);

            if (expr is AST.Literal) return BindLiteral((AST.Literal)expr).WithAccess(access);
            if (expr is AST.VarLikeConstructUse) return BindVarLikeConstructUse((AST.VarLikeConstructUse)expr, access);
            if (expr is AST.BinaryEx) return BindBinaryEx((AST.BinaryEx)expr).WithAccess(access);
            if (expr is AST.AssignEx) return BindAssignEx((AST.AssignEx)expr, access);
            if (expr is AST.UnaryEx) return BindUnaryEx((AST.UnaryEx)expr).WithAccess(access);
            if (expr is AST.GlobalConstUse) return BindGlobalConstUse((AST.GlobalConstUse)expr).WithAccess(access);
            if (expr is AST.IncDecEx) return BindIncDec((AST.IncDecEx)expr).WithAccess(access);
            if (expr is AST.ConditionalEx) return BindConditionalEx((AST.ConditionalEx)expr).WithAccess(access);
            if (expr is AST.ConcatEx) return BindConcatEx((AST.ConcatEx)expr).WithAccess(access);
            
            throw new NotImplementedException(expr.GetType().FullName);
        }

        BoundExpression BindConcatEx(AST.ConcatEx x)
        {
            return new BoundConcatEx(BindArguments(x.Expressions));
        }

        BoundRoutineCall BindFunctionCall(AST.FunctionCall x, BoundAccess access)
        {
            if (access.IsWrite)
            {
                throw new NotSupportedException();
            }

            var boundinstance = (x.IsMemberOf != null) ? BindExpression(x.IsMemberOf) : null;
            var boundargs = BindArguments(x.CallSignature.Parameters);

            if (x is AST.DirectFcnCall)
            {
                var f = (AST.DirectFcnCall)x;
                if (f.IsMemberOf == null)
                {
                    return new BoundFunctionCall(f.QualifiedName, f.FallbackQualifiedName, boundargs)
                        .WithAccess(access);
                }
                else
                {
                    Debug.Assert(f.FallbackQualifiedName.HasValue == false);
                    Debug.Assert(f.QualifiedName.IsSimpleName);
                    return new BoundInstanceMethodCall(boundinstance, f.QualifiedName.Name, boundargs)
                        .WithAccess(access);
                }
            }
            else if (x is AST.DirectStMtdCall)
            {
                var f = (AST.DirectStMtdCall)x;
                Debug.Assert(f.IsMemberOf == null);
                var containingType = f.TypeRef;
                if (containingType is AST.DirectTypeRef)
                {
                    return new BoundStMethodCall(((AST.DirectTypeRef)containingType).GenericQualifiedName, f.MethodName, boundargs)
                        .WithAccess(access);
                }
            }

            throw new NotImplementedException();
        }

        BoundExpression BindConditionalEx(AST.ConditionalEx expr)
        {
            return new BoundConditionalEx(
                BindExpression(expr.CondExpr),
                (expr.TrueExpr != null) ? BindExpression(expr.TrueExpr) : null,
                BindExpression(expr.FalseExpr));
        }

        BoundExpression BindIncDec(AST.IncDecEx expr)
        {
            // bind variable reference
            var varref = (BoundReferenceExpression)BindExpression(expr.Variable, BoundAccess.ReadAndWrite);

            // resolve kind
            UnaryOperationKind kind;
            if (expr.Inc)
                kind = (expr.Post) ? UnaryOperationKind.OperatorPostfixIncrement : UnaryOperationKind.OperatorPrefixIncrement;
            else
                kind = (expr.Post) ? UnaryOperationKind.OperatorPostfixDecrement : UnaryOperationKind.OperatorPrefixDecrement;
            
            //
            return new BoundIncDecEx(varref, kind);
        }

        BoundExpression BindVarLikeConstructUse(AST.VarLikeConstructUse expr, BoundAccess access)
        {
            if (expr is AST.DirectVarUse) return BindDirectVarUse((AST.DirectVarUse)expr, access);
            if (expr is AST.FunctionCall) return BindFunctionCall((AST.FunctionCall)expr, access);
            if (expr is AST.NewEx) return BindNew((AST.NewEx)expr, access);

            throw new NotImplementedException(expr.GetType().FullName);
        }

        BoundExpression BindNew(AST.NewEx x, BoundAccess access)
        {
            Debug.Assert(access.IsRead || access.IsReadRef || access.IsNone);

            if (x.ClassNameRef is AST.DirectTypeRef)
            {
                var qname = x.ClassNameRef.GenericQualifiedName;
                if (!qname.IsGeneric && x.CallSignature.GenericParams.Length == 0)
                    return new BoundNewEx(qname.QualifiedName, BindArguments(x.CallSignature.Parameters))
                        .WithAccess(access);
            }

            throw new NotImplementedException();
        }

        BoundExpression BindDirectVarUse(AST.DirectVarUse expr, BoundAccess access)
        {
            if (expr.IsMemberOf == null)
            {
                return new BoundVariableRef(expr.VarName.Value).WithAccess(access);
            }

            throw new NotImplementedException();
        }

        BoundExpression BindGlobalConstUse(AST.GlobalConstUse expr)
        {
            // translate built-in constants directly
            if (expr.Name == QualifiedName.True) return new BoundLiteral(true);
            if (expr.Name == QualifiedName.False) return new BoundLiteral(false);
            if (expr.Name == QualifiedName.Null) return new BoundLiteral(null);

            // bind constant
            throw new NotImplementedException();
        }

        BoundExpression BindBinaryEx(AST.BinaryEx expr)
        {
            return new BoundBinaryEx(
                BindExpression(expr.LeftExpr, BoundAccess.Read),
                BindExpression(expr.RightExpr, BoundAccess.Read),
                expr.Operation);
        }

        BoundExpression BindUnaryEx(AST.UnaryEx expr)
        {
            return new BoundUnaryEx(BindExpression(expr.Expr, BoundAccess.Read), expr.Operation);
        }

        BoundExpression BindAssignEx(AST.AssignEx expr, BoundAccess access)
        {
            var op = expr.Operation;
            var target = (BoundReferenceExpression)BindExpression(expr.LValue, BoundAccess.Write);
            BoundExpression value;

            if (expr is AST.ValueAssignEx)
            {
                value = BindExpression(((AST.ValueAssignEx)expr).RValue, BoundAccess.Read);
            }
            else
            {
                Debug.Assert(expr is AST.RefAssignEx);
                Debug.Assert(op == AST.Operations.AssignRef);
                target.Access = target.Access.WithWriteRef(0); // note: analysis will write the write type
                value = BindExpression(((AST.RefAssignEx)expr).RValue, BoundAccess.Read.WithEnsureRef());
            }

            // compound assign -> assign
            if (op != AST.Operations.AssignValue && op != AST.Operations.AssignRef)
            {
                AST.Operations binaryop;

                switch (op)
                {
                    case AST.Operations.AssignAdd:
                        binaryop = AST.Operations.Add;
                        break;
                    case AST.Operations.AssignAnd:
                        binaryop = AST.Operations.And;
                        break;
                    case AST.Operations.AssignAppend:
                        binaryop = AST.Operations.Concat;
                        break;
                    case AST.Operations.AssignDiv:
                        binaryop = AST.Operations.Div;
                        break;
                    case AST.Operations.AssignMod:
                        binaryop = AST.Operations.Mod;
                        break;
                    case AST.Operations.AssignMul:
                        binaryop = AST.Operations.Mul;
                        break;
                    case AST.Operations.AssignOr:
                        binaryop = AST.Operations.Or;
                        break;
                    case AST.Operations.AssignPow:
                        binaryop = AST.Operations.Pow;
                        break;
                    //case AST.Operations.AssignPrepend:
                    //    break;
                    case AST.Operations.AssignShiftLeft:
                        binaryop = AST.Operations.ShiftLeft;
                        break;
                    case AST.Operations.AssignShiftRight:
                        binaryop = AST.Operations.ShiftRight;
                        break;
                    case AST.Operations.AssignSub:
                        binaryop = AST.Operations.Sub;
                        break;
                    case AST.Operations.AssignXor:
                        binaryop = AST.Operations.Xor;
                        break;
                    default:
                        throw ExceptionUtilities.UnexpectedValue(op);
                }

                //
                op = AST.Operations.AssignValue;
                value = new BoundBinaryEx(BindExpression(expr.LValue, BoundAccess.Read), value, binaryop)
                    .WithAccess(BoundAccess.Read);
            }

            //
            Debug.Assert(op == AST.Operations.AssignValue || op == AST.Operations.AssignRef);
            
            return new BoundAssignEx(target, value).WithAccess(access);
        }

        static BoundExpression BindLiteral(AST.Literal expr)
        {
            if (expr is AST.IntLiteral) return new BoundLiteral(((AST.IntLiteral)expr).Value);
            if (expr is AST.LongIntLiteral) return new BoundLiteral(((AST.LongIntLiteral)expr).Value);
            if (expr is AST.StringLiteral) return new BoundLiteral(((AST.StringLiteral)expr).Value);
            if (expr is AST.DoubleLiteral) return new BoundLiteral(((AST.DoubleLiteral)expr).Value);
            if (expr is AST.BoolLiteral) return new BoundLiteral(((AST.BoolLiteral)expr).Value);
            if (expr is AST.NullLiteral) return new BoundLiteral(null);
            if (expr is AST.BinaryStringLiteral) return new BoundLiteral(((AST.BinaryStringLiteral)expr).Value);

            throw new NotImplementedException();
        }
    }
}

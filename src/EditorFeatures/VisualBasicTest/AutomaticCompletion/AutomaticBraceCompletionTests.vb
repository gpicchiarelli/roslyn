﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.Editor.UnitTests.AutomaticCompletion
Imports Microsoft.CodeAnalysis.Editor.UnitTests.Extensions
Imports Microsoft.CodeAnalysis.BraceCompletion.AbstractBraceCompletionService

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.AutomaticCompletion
    <Trait(Traits.Feature, Traits.Features.AutomaticCompletion)>
    Public Class AutomaticBraceCompletionTests
        Inherits AbstractAutomaticBraceCompletionTests

        <WpfFact>
        Public Sub TestCreation()
            Using session = CreateSessionASync("$$")
                Assert.NotNull(session)
            End Using
        End Sub

        <WpfFact>
        Public Sub TestInvalidLocation_String()
            Dim code = <code>Class C
    Dim s As String = "$$
End Class</code>

            Using session = CreateSession(code)
                Assert.Null(session)
            End Using
        End Sub

        <WpfFact>
        Public Sub TestInvalidLocation_Comment()
            Dim code = <code>Class C
    ' $$
End Class</code>

            Using session = CreateSession(code)
                Assert.Null(session)
            End Using
        End Sub

        <WpfFact>
        Public Sub TestInvalidLocation_DocComment()
            Dim code = <code>Class C
    ''' $$
End Class</code>

            Using session = CreateSession(code)
                Assert.Null(session)
            End Using
        End Sub

        <WpfFact>
        Public Sub TestTypeParameterMultipleConstraint()
            Dim code = <code>Class C
    Sub Method(Of t As $$
End Class</code>

            Using session = CreateSession(code)
                Assert.NotNull(session)
                CheckStart(session.Session)
            End Using
        End Sub

        <WpfFact>
        Public Sub TestObjectMemberInitializerSyntax()
            Dim code = <code>Class C
    Sub Method()
        Dim a = New With $$
    End Sub
End Class</code>

            Using session = CreateSession(code)
                Assert.NotNull(session)
                CheckStart(session.Session)
            End Using
        End Sub

        <WpfFact>
        Public Sub TestCollectionInitializerSyntax()
            Dim code = <code>Class C
    Sub Method()
        Dim a = New List(Of Integer) From $$
    End Sub
End Class</code>

            Using session = CreateSession(code)
                Assert.NotNull(session)
                CheckStart(session.Session)
            End Using
        End Sub

        Friend Overloads Shared Function CreateSession(code As XElement) As Holder
            Return CreateSessionAsync(code.NormalizedValue())
        End Function

        Friend Overloads Shared Function CreateSessionAsync(code As String) As Holder
            Return CreateSession(
                EditorTestWorkspace.CreateVisualBasic(code),
                CurlyBrace.OpenCharacter, CurlyBrace.CloseCharacter)
        End Function
    End Class
End Namespace

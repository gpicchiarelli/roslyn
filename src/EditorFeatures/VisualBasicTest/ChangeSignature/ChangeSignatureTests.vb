﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.Editor.UnitTests.ChangeSignature

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.ChangeSignature
    <Trait(Traits.Feature, Traits.Features.ChangeSignature)>
    Partial Public Class ChangeSignatureTests
        Inherits AbstractChangeSignatureTests

        <WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/17309")>
        Public Async Function TestNotInLeadingWhitespace() As Task
            Dim markup = "
class C
    [||]
    sub Goo(i as integer, j as integer)
    end sub
end class
"

            Await TestChangeSignatureViaCodeActionAsync(markup, expectedCodeAction:=False)
        End Function

        <WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/17309")>
        Public Async Function TestNotInLeadingTrivia1() As Task
            Dim markup = "
class C
    ' [||]
    sub Goo(i as integer, j as integer)
    end sub
end class
"

            Await TestChangeSignatureViaCodeActionAsync(markup, expectedCodeAction:=False)
        End Function

        <WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/17309")>
        Public Async Function TestNotInLeadingTrivia2() As Task
            Dim markup = "
class C
    [||] '
    sub Goo(i as integer, j as integer)
    end sub
end class
"

            Await TestChangeSignatureViaCodeActionAsync(markup, expectedCodeAction:=False)
        End Function

        <WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/17309")>
        Public Async Function TestNotInLeadingAttributes1() As Task
            Dim markup = "
class C
    [||]<X>
    sub Goo(i as integer, j as integer)
    end sub
end class
"

            Await TestChangeSignatureViaCodeActionAsync(markup, expectedCodeAction:=False)
        End Function

        <WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/17309")>
        Public Async Function TestNotInLeadingAttributes2() As Task
            Dim markup = "
class C
    <X>[||]
    sub Goo(i as integer, j as integer)
    end sub
end class
"

            Await TestChangeSignatureViaCodeActionAsync(markup, expectedCodeAction:=False)
        End Function
    End Class
End Namespace

﻿namespace FSharp.Editing.Documentation



open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text

open FSharp.Editing

/// Represent an Xml documentation block in source code
type XmlDocable =
    | XmlDocable of int * int * string list

module XmlDocParsing =
    open Microsoft.FSharp.Compiler.Range
    open Microsoft.FSharp.Compiler.Ast
        
    let (|ConstructorPats|) = function
        | Pats ps -> ps
        | NamePatPairs(xs, _) -> List.map snd xs

    let rec digNamesFrom = function
        | SynPat.Named (_innerPat,id,_isTheThisVar,_access,_range) -> [id.idText]
        | SynPat.Typed (pat,_type,_range) -> digNamesFrom pat
        | SynPat.Attrib (pat,_attrs,_range) -> digNamesFrom pat
        | SynPat.LongIdent (_lid,_idOpt,_typDeclsOpt,ConstructorPats pats,_access,_range) -> 
            pats |> List.collect digNamesFrom 
        | SynPat.Tuple (pats,_range) -> pats |> List.collect digNamesFrom 
        | SynPat.StructTuple(pats,_range) -> pats |> List.collect digNamesFrom
        | SynPat.Paren (pat,_range) -> digNamesFrom pat
        | SynPat.OptionalVal (id, _) -> [id.idText]
        | SynPat.Or _           // no one uses ors in fun decls
        | SynPat.Ands _         // no one uses ands in fun decls
        | SynPat.ArrayOrList _  // no one uses this in fun decls
        | SynPat.Record _       // no one uses this in fun decls
        | SynPat.Null _
        | SynPat.Const _
        | SynPat.Wild _
        | SynPat.IsInst _
        | SynPat.QuoteExpr _
        | SynPat.DeprecatedCharRange _
        | SynPat.InstanceMember _
        | SynPat.FromParseError _ -> []

    let getXmlDocablesImpl(sourceCodeLinesOfTheFile: string [], input: ParsedInput option) =
        let indentOf (lineNum: int) =
            let mutable i = 0
            let line = sourceCodeLinesOfTheFile.[lineNum-1] // -1 because lineNum reported by xmldocs are 1-based, but array is 0-based
            while i < line.Length && line.Chars(i) = ' ' do
                i <- i + 1
            i

        let isEmptyXmlDoc (preXmlDoc: PreXmlDoc) =
            match preXmlDoc.ToXmlDoc() with 
            | XmlDoc [||] -> true
            | XmlDoc [|x|] when x.Trim() = "" -> true
            | _ -> false

        let rec getXmlDocablesSynModuleDecl = function
            | SynModuleDecl.NestedModule(_,  _, synModuleDecls, _, _) -> 
                (synModuleDecls |> List.collect getXmlDocablesSynModuleDecl)
            | SynModuleDecl.Let(_, synBindingList, range) -> 
                let anyXmlDoc = 
                    synBindingList |> List.exists (fun (SynBinding.Binding(_, _, _, _, _, preXmlDoc, _, _, _, _, _, _)) -> 
                        not <| isEmptyXmlDoc preXmlDoc)
                if anyXmlDoc then [] else
                let synAttributes = 
                    synBindingList |> List.collect (fun (SynBinding.Binding(_, _, _, _, a, _, _, _, _, _, _, _)) -> a)
                let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) range
                let line = fullRange.StartLine 
                let indent = indentOf line
                [ for SynBinding.Binding(_, _, _, _, _, _, synValData, synPat, _, _, _, _) in synBindingList do
                      match synValData with
                      | SynValData(_memberFlagsOpt, SynValInfo(args, _), _) when not (List.isEmpty args) -> 
                          let parameters =
                              args 
                              |> List.collect (
                                    List.collect (fun (SynArgInfo(_, _, ident)) -> 
                                        match ident with 
                                        | Some ident -> [ident.idText]
                                        | None -> []))
                          match parameters with
                          | [] ->
                              let paramNames = digNamesFrom synPat
                              yield! paramNames
                          | _ :: _ ->
                             yield! parameters
                      | _ -> () ]
                |> fun paramNames -> [ XmlDocable(line,indent,paramNames) ]
            | SynModuleDecl.Types(synTypeDefnList, _) -> (synTypeDefnList |> List.collect getXmlDocablesSynTypeDefn)
            | SynModuleDecl.NamespaceFragment(synModuleOrNamespace) -> getXmlDocablesSynModuleOrNamespace synModuleOrNamespace
            | SynModuleDecl.ModuleAbbrev _
            | SynModuleDecl.DoExpr _
            | SynModuleDecl.Exception _
            | SynModuleDecl.Open _
            | SynModuleDecl.Attributes _
            | SynModuleDecl.HashDirective _ -> []

        and getXmlDocablesSynModuleOrNamespace (SynModuleOrNamespace(_, _,  _, synModuleDecls, _, _, _, _)) =
            (synModuleDecls |> List.collect getXmlDocablesSynModuleDecl)

        and getXmlDocablesSynTypeDefn (SynTypeDefn.TypeDefn(ComponentInfo(synAttributes, _, _, _, preXmlDoc, _, _, compRange), synTypeDefnRepr, synMemberDefns, tRange)) =
            let stuff = 
                match synTypeDefnRepr with
                | SynTypeDefnRepr.ObjectModel(_, synMemberDefns, _) -> (synMemberDefns |> List.collect getXmlDocablesSynMemberDefn)
                | SynTypeDefnRepr.Simple(_synTypeDefnSimpleRepr, _range) -> []
                | SynTypeDefnRepr.Exception _ -> []
            let docForTypeDefn = 
                if isEmptyXmlDoc preXmlDoc then
                    let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) (unionRanges compRange tRange)
                    let line = fullRange.StartLine 
                    let indent = indentOf line
                    [XmlDocable(line,indent,[])]
                else []
            docForTypeDefn @ stuff @ (synMemberDefns |> List.collect getXmlDocablesSynMemberDefn)

        and getXmlDocablesSynMemberDefn = function
            | SynMemberDefn.Member(SynBinding.Binding(_, _, _, _, synAttributes, preXmlDoc, _, synPat, _, _, _, _), memRange) -> 
                if isEmptyXmlDoc preXmlDoc then
                    let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) memRange
                    let line = fullRange.StartLine 
                    let indent = indentOf line
                    let paramNames = digNamesFrom synPat 
                    [XmlDocable(line,indent,paramNames)]
                else []
            | SynMemberDefn.AbstractSlot(ValSpfn(synAttributes, _, _, _, SynValInfo(args, _), _, _, preXmlDoc, _, _, _), _, range) -> 
                if isEmptyXmlDoc preXmlDoc then
                    let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) range
                    let line = fullRange.StartLine 
                    let indent = indentOf line
                    let paramNames = args |> List.collect (fun az -> az |> List.choose (fun (SynArgInfo(_synAttributes, _, idOpt)) -> match idOpt with | Some id -> Some(id.idText) | _ -> None))
                    [XmlDocable(line,indent,paramNames)]
                else []
            | SynMemberDefn.Interface(_synType, synMemberDefnsOption, _range) -> 
                match synMemberDefnsOption with 
                | None -> [] 
                | Some(x) -> x |> List.collect getXmlDocablesSynMemberDefn
            | SynMemberDefn.NestedType(synTypeDefn, _, _) -> getXmlDocablesSynTypeDefn synTypeDefn
            | SynMemberDefn.AutoProperty(synAttributes, _, _, _, _, _, _, _, _, _, range) -> 
                let fullRange = synAttributes |> List.fold (fun r a -> unionRanges r a.Range) range
                let line = fullRange.StartLine 
                let indent = indentOf line
                [XmlDocable(line, indent, [])]
            | SynMemberDefn.Open _
            | SynMemberDefn.ImplicitCtor _
            | SynMemberDefn.ImplicitInherit _
            | SynMemberDefn.Inherit _
            | SynMemberDefn.ValField _
            | SynMemberDefn.LetBindings _ -> []

        and getXmlDocablesInput input =
            match input with
            | ParsedInput.ImplFile(ParsedImplFileInput(_, _, _, _, _, symModules, _))-> 
                symModules |> List.collect getXmlDocablesSynModuleOrNamespace
            | ParsedInput.SigFile _ -> []

        async {
            // Get compiler options for the 'project' implied by a single script file
            match input with
            | Some input -> 
                return getXmlDocablesInput input
            | None ->
                // Should not fail here, just in case 
                return []
        }

//module XmlDocComment =
//    
//
//
//
//    let private ws (s: string, pos) :(string * int) option = 
//        let res = s.TrimStart()
//        Some (res, pos + (s.Length - res.Length))
//
//    let private str (prefix: string) (s: string, pos) :(string * int) option =
//        match s.StartsWith prefix with
//        | true -> 
//            let res = s.Substring prefix.Length
//            Some (res, pos + (s.Length - res.Length))
//        | _ -> None
//
//    let private eol (s: string, pos):(string * 'a) option = 
//        match s with
//        | "" -> Some ("", pos)
//        | _ -> None
//
//    let inline private (>=>) f g = f >> Option.bind g
//    
//    // if it's a blank XML comment with trailing "<", returns Some (index of the "<"), otherwise returns None
//    let isBlank (s: string) : int option =
//        let parser = ws >=> str "///" >=> ws >=> str "<" >=> eol
//        let res = parser (s.TrimEnd(), 0) |> Option.map snd |> Option.map (fun x -> x - 1)
//        res

module XmlDocComment = 
    
    let private ws (tl:TextLine, pos:int) : (TextLine * int) option  = 
        let res = tl.TrimStart ()
        Some (res, pos + (tl.Length - res.Length))

    let private str (prefix:TextLine) (tl:TextLine,pos:int) : (TextLine * int) option  =
        match tl.StartsWith prefix with
        | true -> 
            let res = tl.SubSection prefix.Length
            Some (res, pos + (tl.Length - res.Length))
        | _ -> None

    let private eol (tl: TextLine, pos):(TextLine * 'a) option = 
        if TextLine.isEmpty tl then  
            Some (TextLine(), pos)
        else None
            
    let inline private (>=>) f g = f >> Option.bind g

    // if it's a blank XML comment with trailing "<", returns Some (index of the "<"), otherwise returns None
    let isBlank (tl:TextLine) =
        let parser = ws >=> str (TextLine.From "///") >=> ws >=> str (TextLine.From "<") >=> eol
        let res = parser (tl.TrimEnd(), 0) |> Option.map snd |> Option.map (fun x -> x - 1)
        res




// TODO - reimplement this functionality using textlines and sourceText
//    let isBlank (line:TextLine) =

module XmlDocParser =
    /// Get the list of Xml documentation from current source code
    let getXmlDocables (sourceCodeOfTheFile, input) =
        let sourceCodeLinesOfTheFile = String.getLines sourceCodeOfTheFile
        XmlDocParsing.getXmlDocablesImpl (sourceCodeLinesOfTheFile, input)


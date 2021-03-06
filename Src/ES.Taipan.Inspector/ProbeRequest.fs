﻿namespace ES.Taipan.Inspector

open System
open System.Text.RegularExpressions
open System.Collections.Generic
open ES.Taipan.Infrastructure.Network
open ES.Taipan.Infrastructure.Text
open ES.Taipan.Infrastructure.Text
open ES.Taipan.Crawler.WebScrapers
open ES.Taipan.Crawler

type ProbeParameterType =
    | QUERY
    | DATA
    | HEADER

    override this.ToString() =
        match this with
        | QUERY -> "Query"
        | DATA -> "Data"
        | HEADER -> "Header"

type ProbeParameter() as this =
    let mutable _value = String.Empty
    let mutable _filename = None : String option
    let mutable _isUnderTest = false
    let mutable _stateSaved = false
    
    member val Id = Guid.NewGuid()
    member val Name = String.Empty with get, set
    member val Value = String.Empty with get, set
    member val ExpectedValues = List.empty<String> with get, set
    member val Type = ProbeParameterType.QUERY with get, set
    member val Filename : String option = None with get, set
    member val AlterValue = fun (x: String) -> this.Value <- x with get, set
    member val State : Object option = None with get, set
    member val IsUnderTest = false with get, set
    
    member this.SaveState() =
        if not _stateSaved then
            _value <- this.Value
            _filename <- this.Filename
            _isUnderTest <- this.IsUnderTest
            _stateSaved <- true
            
    member this.RestoreState() =
        if _stateSaved then
            this.Value <- _value
            this.Filename <- _filename
            this.IsUnderTest <- _isUnderTest
            _stateSaved <- false

    member this.IsStateSaved() =
        _stateSaved

    member this.AcquireValue(parameter: ProbeParameter) =
        if parameter.IsStateSaved() then
            let (savedValue, savedFilename) = (parameter.Value, parameter.Filename)
            // copy original saved value
            parameter.RestoreState()        
            this.Value <- parameter.Value
            this.Filename <- parameter.Filename

            // save state again
            parameter.SaveState()
            this.SaveState()

            // restore effective value
            parameter.Value <- savedValue
            this.Value <- savedValue
            parameter.Filename <- savedFilename
            this.Filename <- savedFilename
        else
            this.Value <- parameter.Value
            this.Filename <- parameter.Filename

        this.State <- parameter.State
        
            
    override this.ToString() =
        String.Format("[{0}] {1} = {2}", this.Type, this.Name, this.Value)

type ProbeRequest(testRequest: TestRequest) = 
    let _parameters = new List<ProbeParameter>()
    let _webRequest = testRequest.WebRequest
    let _webResponse = testRequest.WebResponse

    let composeData(filter: ProbeParameter -> Boolean) =
        let dataItems =
            _parameters
            |> Seq.filter(filter)
            |> Seq.map(fun param ->
                param.Name + "=" + param.Value
            )
        String.Join("&", dataItems)
        
    do 
        // get query parameters
        if  _webRequest.HttpRequest.Uri.Query.Length > 1 then
            WebUtility.getParametersFromData(_webRequest.HttpRequest.Uri.Query.Substring(1))
            |> Seq.iter(fun (name, value) ->
                _parameters.Add(new ProbeParameter(Name = name, Value = value, Type = QUERY))
            )

        // get data parameters
        match HttpUtility.tryGetHeader("Content-Type", _webRequest.HttpRequest.Headers) with
        | Some hdr when hdr.Value.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ->             
            let boundaryMatch = Regex.Match(hdr.Value, "boundary=(.+)", RegexOptions.Singleline)
            if boundaryMatch.Success then
                let boundary = boundaryMatch.Groups.[1].Value.Trim()                
                for item in _webRequest.HttpRequest.Data.Split([|boundary|], StringSplitOptions.RemoveEmptyEntries) do
                    if not <| item.Equals("--", StringComparison.Ordinal) then
                        // parse header
                        let itemHeader = item.Split([|"\r\n\r\n"|], StringSplitOptions.None).[0].Trim()
                        let mutable name : String option = None
                        let mutable filename : String option = None
                        for line in itemHeader.Split([|"\r\n"|], StringSplitOptions.RemoveEmptyEntries) do
                            if name.IsNone then name <- RegexUtility.getHtmlAttributeValueFromChunk(line, "name")
                            if filename.IsNone then filename <- RegexUtility.getHtmlAttributeValueFromChunk(line, "filename")

                        if name.IsSome then
                            // parse content
                            let chunks = item.Split([|"\r\n\r\n"|], StringSplitOptions.None)
                            if chunks.Length > 2 then
                                let itemContent = chunks.[1].Trim()
                                _parameters.Add(new ProbeParameter(Name = name.Value, Value = itemContent, Filename = filename, Type = DATA))
            else
                if not(String.IsNullOrWhiteSpace(_webRequest.HttpRequest.Data)) then
                    WebUtility.getParametersFromData(_webRequest.HttpRequest.Data)
                    |> Seq.iter(fun (name, value) ->
                        _parameters.Add(new ProbeParameter(Name = name, Value = value, Type = DATA))
                    )
        | _ ->
            if not(String.IsNullOrWhiteSpace(_webRequest.HttpRequest.Data)) then
                WebUtility.getParametersFromData(_webRequest.HttpRequest.Data)
                |> Seq.iter(fun (name, value) ->
                    _parameters.Add(new ProbeParameter(Name = name, Value = value, Type = DATA))
                )

        // get headers parameters
        _webRequest.HttpRequest.Headers
        |> Seq.iter(fun httpHeader ->
            _parameters.Add(new ProbeParameter(Name = httpHeader.Name, Value = httpHeader.Value, Type = HEADER))
        )
        
    member val TestRequest = testRequest with get
    member val WebResponse : WebResponse option = None with get, set

    member this.GetParameters() =
        _parameters |> Seq.readonly

    member this.EnsureConsistencyOnPasswordTypeParameter(parameter: ProbeParameter) =
        if this.TestRequest.RequestType = TestRequestType.CrawledPage then
            let parsedHtml = this.TestRequest.GetData<WebLink>().ParsedHtmlCode
            
            // retrieve all the parameter names that are of type password
            let passwordParameterNames = new List<String>()
            RegexUtility.getTagsAttributes(parsedHtml)
            |> Seq.iter(fun (tagName, attributes) ->
                let mutable inputName = String.Empty
                let mutable passwordFound = false
                attributes
                |> Seq.iter(fun (name, value) ->
                    if name.Equals("name", StringComparison.OrdinalIgnoreCase)
                    then inputName <- value

                    if name.Equals("type", StringComparison.OrdinalIgnoreCase) && value.Equals("password", StringComparison.OrdinalIgnoreCase)
                    then passwordFound <- true
                )

                if (passwordFound)
                then passwordParameterNames.Add(inputName)
            )

            if passwordParameterNames.Contains(parameter.Name) then
                // for all password parameters I have to set the same value
                this.GetParameters()
                |> Seq.iter(fun otherParameter ->
                    if passwordParameterNames.Contains(otherParameter.Name) && not(otherParameter.Name.Equals(parameter.Name, StringComparison.Ordinal))then
                        // configure probe with same value
                        otherParameter.Value <- parameter.Value
                        otherParameter.ExpectedValues <- parameter.ExpectedValues
                        otherParameter.IsUnderTest <- true
                )

    member this.SaveState() =
        this.GetParameters() 
        |> Seq.iter(fun p -> p.SaveState())

    member this.RestoreState() =
        this.GetParameters() 
        |> Seq.iter(fun p -> p.RestoreState())    

    member this.AddParameter(parameter: ProbeParameter) =        
        match this.GetParameters() |> Seq.tryFind(fun p -> p.Name.Equals(parameter.Name, StringComparison.Ordinal)) with
        | Some storedParameter -> storedParameter.AcquireValue(parameter)
        | None -> _parameters.Add(parameter)       

    member this.BuildHttpRequest(copySource: Boolean) =
        let clonedRequest = HttpRequest.DeepClone(_webRequest.HttpRequest)

        if not copySource then
            clonedRequest.Source <- None

        // create new uri
        let uriBuilder = new UriBuilder(clonedRequest.Uri)
        uriBuilder.Query <- composeData(fun p -> p.Type = QUERY)
        clonedRequest.Uri <- uriBuilder.Uri

        // set header
        clonedRequest.Headers.Clear()
        this.GetParameters()
        |> Seq.filter(fun p -> p.Type = HEADER)
        |> Seq.iter(fun parameter ->
            clonedRequest.Headers.Add(new HttpHeader(Name = parameter.Name, Value = parameter.Value))
        )

        // create data
        match HttpUtility.tryGetHeader("Content-Type", clonedRequest.Headers) with
        | Some hdr when hdr.Value.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) -> 
            let (effectiveContentType, data) = 
                this.GetParameters()
                |> Seq.filter(fun p -> p.Type = DATA)
                |> Seq.map(fun p -> 
                    match p.Filename with
                    | Some filename -> (Some "file", Some filename, p.Name + "=" + p.Value)
                    | None -> (None, None, p.Name + "=" + p.Value)
                )
                |> FormLinkScraperUtility.createMultipartDataString
            
            clonedRequest.Data <- data
            clonedRequest.Headers.Remove(hdr) |> ignore
            clonedRequest.Headers.Add(new HttpHeader(Name="Content-Type", Value=effectiveContentType))
        | _ -> clonedRequest.Data <- composeData(fun p -> p.Type = DATA)

        clonedRequest

    member this.BuildHttpRequest() =
        this.BuildHttpRequest(false)

    override this.ToString() =
        this.TestRequest.ToString()

[<AutoOpen>]
module ProbeRequestUtility =
    let getParameter(parameter: ProbeParameter, probeRequest: ProbeRequest) =
        probeRequest.GetParameters() |> Seq.find(fun p -> p.Name.Equals(parameter.Name, StringComparison.Ordinal) && p.Type = parameter.Type)
﻿// Author : Yan Cui (twitter @theburningmonk)

// Email : theburningmonk@gmail.com
// Blog  : http://theburningmonk.com

namespace Amazon.DynamoDBv2.DataModel

open System
open System.Linq
open System.Runtime.CompilerServices
open Amazon.DynamoDBv2.DataModel
open Amazon.DynamoDBv2.DocumentModel
open DynamoDb.SQL

[<AutoOpen>]
module Ctx =
    let (|GetQueryConfig|) (query : DynamoQuery) = 
        match query with
        | { From    = From table
            Where   = Where(QueryCondition keyConditions)
            Action  = Select(SelectAttributes attributes)
            Order   = order
            Options = opts } -> 
            let config = new QueryOperationConfig(AttributesToGet = attributes)

            let allAttributes =  
                match tryGetQueryIndex opts with
                | Some(idxName, allAttributes) 
                    -> config.IndexName <- idxName
                       allAttributes
                | _ -> true

            // you cannot specify both AttributesToGet and SPECIFIC_ATTRIBUTES 
            // in Select for more details, see 
            // http://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html
            config.Select <- 
                match attributes, allAttributes with
                | null, false -> SelectValues.AllProjectedAttributes
                | null, true  -> SelectValues.AllAttributes
                | _, _        -> SelectValues.SpecificAttributes
               
            let queryFilter = new QueryFilter()
            keyConditions 
            |> List.iter (fun (attr, cond) -> 
                queryFilter.AddCondition(attr, cond.ToCondition()))
            config.Filter <- queryFilter

            match order with 
            | Some(Asc)  -> config.BackwardSearch <- false
            | Some(Desc) -> config.BackwardSearch <- true
            | None       -> ()

            config.ConsistentRead <- isConsistentRead opts
            match tryGetQueryPageSize opts with 
            | Some n -> config.Limit <- n 
            | _ -> ()
               
            config
        | { Action = Count } -> 
            raise <| NotSupportedException("Count is not supported by DynamoDBContext")
        
    type ScanOperationConfig with
        member this.SplitIntoSegments () =
            let makeSegment n =
                new ScanOperationConfig(
                    AttributesToGet = this.AttributesToGet,
                    Filter          = this.Filter,
                    Limit           = this.Limit,
                    TotalSegments   = this.TotalSegments,
                    Select          = this.Select,
                    Segment         = n)

            [| 0..this.TotalSegments - 1 |] 
            |> Array.map makeSegment

    let (|GetScanConfigs|) (scan : DynamoScan) =
        match scan with
        | { From    = From table
            Where   = where
            Action  = Select(SelectAttributes attributes)
            Options = opts } ->
            let config = new ScanOperationConfig(AttributesToGet = attributes)

            // optionally set the scan filter if applicable
            match where with
            | Some(Where(ScanCondition scanFilters)) -> 
                let scanFilter = new ScanFilter()
                scanFilters 
                |> List.iter (fun (attr, cond) -> 
                    scanFilter.AddCondition(attr, cond.ToCondition()))

                config.Filter <- scanFilter
            | _ -> ()

            // you cannot specify both AttributesToGet and SPECIFIC_ATTRIBUTES in Select
            // for more details, see http://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Scan.html
            config.Select <- 
                match attributes with
                | null -> SelectValues.AllAttributes
                | _    -> SelectValues.SpecificAttributes
               
            match tryGetScanPageSize opts with 
            | Some n -> config.Limit <- n 
            | _ -> ()
            config.TotalSegments <- getScanSegments opts

            config.SplitIntoSegments()
        | { Action = Count } -> 
            raise <| NotSupportedException("Count is not supported by DynamoDBContext")

[<AutoOpen>]
module ContextExt = 
    type DynamoDBContext with
        member this.ExecQuery (query : string) =
            let dynamoQuery = parseDynamoQuery query
            match dynamoQuery with
            | { Limit = Some(Limit n) } & GetQueryConfig config -> 
                // NOTE: the reason the Seq.take is needed here is that the limit set in the 
                // Query operation limit is 'per page', and DynamoDBContext lazy-loads all results
                // see https://forums.aws.amazon.com/thread.jspa?messageID=375136&#375136
                (this.FromQuery config).Take n
            | GetQueryConfig config -> this.FromQuery config
            | _ -> 
                let errMsg = sprintf "Not a valid query operation : %s" query
                raise <| InvalidQueryException errMsg

        member this.ExecScan (query : string) =
            let dynamoScan = parseDynamoScan query
            let maxResults = 
                match dynamoScan.Limit with 
                | Some(Limit n) -> n 
                | _ -> Int32.MaxValue

            let scanConfigs = 
                match dynamoScan with 
                | GetScanConfigs configs -> configs
                | _ -> 
                    let errMsg = sprintf "Not a valid scan operation : %s" query
                    raise <| InvalidScanException errMsg
                        
            let results =
                scanConfigs 
                |> Seq.map (fun config -> 
                    async { 
                        return (this.FromScan config).Take maxResults 
                    })
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Seq.collect id

            // NOTE: the reason the Enumerable.Take is needed here is that the limit set in the 
            // Scan operation limit is 'per page', and DynamoDBContext lazy-loads all results
            // see https://forums.aws.amazon.com/thread.jspa?messageID=375136&#375136.
            // Additionally, Seq.take excepts when there are insufficient number of elements, but
            // Enumerable.Take (from LINQ) does not and is the desired behaviour here.
            results.Take maxResults

[<Extension>]
[<AbstractClass>]
[<Sealed>]
type DynamoDBContextExt =
    [<Extension>]
    static member ExecQuery (ctx : DynamoDBContext, query : string) = 
        ctx.ExecQuery(query)

    [<Extension>]
    static member ExecScan (ctx : DynamoDBContext, query : string) = 
        ctx.ExecScan(query)
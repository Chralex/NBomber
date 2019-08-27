﻿namespace NBomber.FSharp

open System
open System.IO
open System.Threading
open System.Threading.Tasks

open Serilog

open NBomber
open NBomber.Contracts
open NBomber.Configuration
open NBomber.Domain
open NBomber.Infra.Dependency
open NBomber.DomainServices

type ConnectionPool =

    static member create<'TConnection>(name: string,
                                       openConnection: unit -> 'TConnection,
                                       ?closeConnection: 'TConnection -> unit,
                                       ?connectionsCount: int) =        
        { PoolName = name
          OpenConnection = openConnection
          CloseConnection = closeConnection
          ConnectionsCount = connectionsCount
          AliveConnections = Array.empty }
          :> IConnectionPool<'TConnection>

    static member none =
        { PoolName = Constants.EmptyPoolName
          OpenConnection = ignore
          CloseConnection = None
          ConnectionsCount = None
          AliveConnections = Array.empty }
          :> IConnectionPool<unit>

    static member internal internalNone<'TConnection> () =
        { PoolName = Constants.EmptyPoolName
          OpenConnection = fun _ -> Unchecked.defaultof<'TConnection>
          CloseConnection = None
          ConnectionsCount = None
          AliveConnections = Array.empty }
          :> IConnectionPool<'TConnection>

type Step =

    static member private getRepeatCount (repeatCount: int option) = 
        match defaultArg repeatCount Constants.DefaultRepeatCount with
        | x when x <= 0 -> 1
        | x             -> x

    static member create (name: string,                          
                          pool: IConnectionPool<'TConnection>,
                          execute: StepContext<'TConnection> -> Task<Response>,                          
                          ?repeatCount: int) =
        
        let p = pool :?> ConnectionPool<'TConnection>        

        let newOpen = fun () -> p.OpenConnection() :> obj
        
        let newClose = 
            match p.CloseConnection with
            | Some func -> Some <| fun (c: obj) -> c :?> 'TConnection |> func
            | None      -> None

        let newPool = { PoolName = p.PoolName
                        OpenConnection = newOpen
                        CloseConnection = newClose
                        ConnectionsCount = p.ConnectionsCount
                        AliveConnections = Array.empty }

        let newExecute =
            fun (context: StepContext<obj>) ->
                let newContext = { CorrelationId = context.CorrelationId
                                   CancellationToken = context.CancellationToken
                                   Connection = context.Connection :?> 'TConnection
                                   Data = context.Data }
                execute(newContext)

        { StepName = name
          ConnectionPool = newPool
          Execute = newExecute
          CurrentContext = None
          RepeatCount = Step.getRepeatCount(repeatCount) }
          :> IStep

    static member create (name: string,                          
                          execute: StepContext<'TConnection> -> Task<Response>,
                          ?repeatCount: int) =

        Step.create(name, 
                    ConnectionPool.internalNone<'TConnection>(),
                    execute,                     
                    Step.getRepeatCount(repeatCount))

type Assertion =

    static member forStep (stepName, assertion: Statistics -> bool, ?label: string) =
        { StepName = stepName
          ScenarioName = ""
          AssertFunc = assertion
          Label = label } 
          :> IAssertion

module Scenario =    

    /// Creates scenario with steps which will be executed sequentially.
    let create (name: string) (steps: IStep list): Contracts.Scenario =
        { ScenarioName = name
          TestInit = Unchecked.defaultof<_>
          TestClean = Unchecked.defaultof<_>
          Steps = List.toArray(steps)
          Assertions = Array.empty
          ConcurrentCopies = Constants.DefaultConcurrentCopies          
          WarmUpDuration = TimeSpan.FromSeconds(Constants.DefaultWarmUpDurationInSec)
          Duration = TimeSpan.FromSeconds(Constants.DefaultScenarioDurationInSec) }

    let withTestInit (initFunc: CancellationToken -> Task<unit>) (scenario: Contracts.Scenario) =
        { scenario with TestInit = Some(fun token -> initFunc(token) :> Task) }

    let withTestClean (cleanFunc: CancellationToken -> Task<unit>) (scenario: Contracts.Scenario) =
        { scenario with TestClean = Some(fun token -> cleanFunc(token) :> Task) }

    let withAssertions (assertions: IAssertion list) (scenario: Contracts.Scenario) =
        let asrts = assertions
                    |> Seq.cast<Domain.Assertion>
                    |> Seq.map(fun x -> { x with ScenarioName = scenario.ScenarioName} :> IAssertion)
                    |> Seq.toArray

        { scenario with Assertions = asrts }

    let withConcurrentCopies (concurrentCopies: int) (scenario: Contracts.Scenario) =
        { scenario with ConcurrentCopies = concurrentCopies }    

    let withWarmUpDuration (duration: TimeSpan) (scenario: Contracts.Scenario) =
        { scenario with WarmUpDuration = duration }

    let withDuration (duration: TimeSpan) (scenario: Contracts.Scenario) =
        { scenario with Duration = duration }

module NBomberRunner =    

    /// Registers scenarios in NBomber environment. Scenarios will be run in parallel.
    let registerScenarios (scenarios: Contracts.Scenario list) =
        { Scenarios = List.toArray(scenarios)
          NBomberConfig = None
          ReportFileName = None
          ReportFormats = Domain.Constants.AllReportFormats
          StatisticsSink = None }

    let withReportFileName (reportFileName: string) (context: NBomberContext) =
        { context with ReportFileName = Some reportFileName }

    let withReportFormats (reportFormats: ReportFormat list) (context: NBomberContext) =
        { context with ReportFormats = reportFormats }

    let loadConfig (path: string) (context: NBomberContext) =
        let config = path |> File.ReadAllText |> NBomberConfig.parse
        { context with NBomberConfig = Some config }

    let saveStatisticsTo (statisticsSink: IStatisticsSink) (context: NBomberContext) =
        { context with StatisticsSink = Some statisticsSink }

    let run (context: NBomberContext) =
        NBomberRunner.runAs Process context

    let rec runInConsole (context: NBomberContext) =
        NBomberRunner.runAs Console context
        |> ignore
        Log.Information("Repeat the same test one more time? (y/n)")

        let userInput = Console.ReadLine()
        let repeat = List.contains userInput ["y"; "Y"; "yes"; "Yes"]
        if repeat then runInConsole context

    let runTest (context: NBomberContext) =
        NBomberRunner.runAs Test context
        |> ignore
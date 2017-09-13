namespace VSharp

open VSharp.Terms

module internal Merging =

    type private MergeType =
        | StructMerge
        | BoolMerge
        | DefaultMerge

    let private mergeTypeOf term =
        match term.term with
        | Struct _ -> StructMerge
        | _ when IsBool term -> BoolMerge
        | _ -> DefaultMerge

    // TODO: This is a pretty performance-critical function. We should store the result into the union itself.
    let internal guardOf term =
        match term.term with
        | Terms.GuardedValues(gs, _) -> disjunction term.metadata gs
        | _ -> Terms.MakeTrue term.metadata

    let private boolMerge = function
        | [] -> []
        | [_] as gvs -> gvs
        | [(g1, v1); (g2, v2)] -> [(g1 ||| g2, (g1 &&& v1) ||| (g2 &&& v2))]
        | (g, v)::gvs ->
            let guard = List.fold (|||) g (List.map fst gvs) in
            let value = List.fold (fun acc (g, v) -> acc ||| (g &&& v)) (g &&& v) gvs in
            [(guard, value)]

    let rec private structMerge = function
        | [] -> []
        | [_] as gvs -> gvs
        | (x :: _) as gvs ->
            let t = x |> snd |> TypeOf in
            assert(gvs |> Seq.map (snd >> TypeOf) |> Seq.forall ((=) t))
            let gs, vs = List.unzip gvs in
            let extractFields = term >> function
                | Struct(fs, _) -> fs
                | t -> "Expected struct, got " + (toString t) |> internalfail
            in
            let fss = vs |> List.map extractFields in
            let merged = Heap.merge gs fss mergeCells in
            let guard = disjunction Metadata.empty gs in
            [(True, Struct merged t Metadata.empty)]

    and private simplify gvs =
        let rec loop gvs out =
            match gvs with
            | [] -> out
            | (True, v)::gvs' -> [List.head gvs]
            | (False, v)::gvs' -> loop gvs' out
            | (g, UnionT us)::gvs' when not (List.isEmpty us) ->
                let guarded = us |> List.map (fun (g', v) -> (g &&& g', v)) in
                loop gvs' (List.append (simplify guarded) out)
            | gv::gvs' -> loop gvs' (gv::out)
        loop gvs []

    and internal mergeSame = function
        | [] -> []
        | [_] as xs -> xs
        | [(g1, v1); (g2, v2)] as gvs -> if v1 = v2 then [(g1 ||| g2, v1)] else gvs
        | gvs ->
            let rec loop gvs out =
                match gvs with
                | [] -> out
                | (g, v)::gvs' ->
                    let eq, rest = List.partition (snd >> (=) v) gvs' in
                    let joined = List.fold (|||) g (List.map fst eq)
                    match joined with
                    | True -> [(joined, v)]
                    | False -> loop rest out
                    | _ -> loop rest ((joined, v)::out)
            loop gvs []

    and private typedMerge gvs t =
        match t with
        | BoolMerge -> boolMerge gvs
        | StructMerge -> structMerge gvs
        // TODO: merge arrays too
        | DefaultMerge -> gvs

    and propagateGuard g v =
        match v.term with
        | Struct(contents, t) ->
            let contents' = Heap.map (fun _ (v, c, m) -> (merge [(g, v)], c, m)) contents in
            (Terms.True, Struct contents' t v.metadata)
        | Array(lower, constant, contents, lengths, t) ->
            let contents' = Heap.map (fun _ (v, c, m) -> (merge [(g, v)], c, m)) contents in
            (Terms.True, Array lower constant contents' lengths t v.metadata)
        | _ -> (g, v)

    and private compress = function
        | [] -> []
        | [(g, v)] as gvs ->
            match g with
            | True -> gvs
            | _ -> [propagateGuard g v]
        | [(_, v1); (_, v2)] as gvs when mergeTypeOf v1 = mergeTypeOf v2 -> typedMerge (mergeSame gvs) (mergeTypeOf v1)
        | [_; _] as gvs -> gvs
        | gvs ->
            gvs
                |> mergeSame
                |> List.groupBy (snd >> mergeTypeOf)
                |> List.map (fun (t, gvs) -> typedMerge gvs t)
                |> List.concat

    and internal merge gvs =
        match compress (simplify gvs) with
        | [(True, v)] -> v
        | [(g, v)] when Terms.IsBool v -> g &&& v
        | gvs' -> Union Metadata.empty gvs'

    and internal mergeCells gcs =
        let foldCell (acc1, acc2, acc3) (g, (v, c, m)) = ((g, v)::acc1, min acc2 c, max acc3 m) in
        let gvs, c, m = gcs |> List.fold foldCell ([], System.UInt32.MaxValue, System.UInt32.MinValue) in
        (merge gvs, c, m)

    let internal merge2Terms g h u v =
        let g = guardOf u &&& g in
        let h = guardOf v &&& h in
        match g, h with
        | _, _ when u = v -> u
        | True, _
        | _, False
        | False, _
        | _, True -> v
        | ErrorT _, _ -> g
        | _, ErrorT _ -> h
        | _ -> merge [(g, u); (h, v)]

    let internal merge2Cells g h ((u, cu, mu) as ucell : MemoryCell<Term>) ((v, cv, mv) as vcell : MemoryCell<Term>) =
        let g = guardOf u &&& g in
        let h = guardOf v &&& h in
        match g, h with
        | _, _ when u = v -> (u, min cu cv, min mu mv)
        | True, _
        | _, False -> ucell
        | False, _
        | _, True -> vcell
        | ErrorT _, _ -> (g, cu, mu)
        | _, ErrorT _ -> (h, cv, mv)
        | _ -> mergeCells [(g, ucell); (h, vcell)]

    let internal merge2States condition1 condition2 state1 state2 =
        match condition1, condition2 with
        | True, _ -> state1
        | False, _ -> state2
        | _, True -> state2
        | _, False -> state1
        | _ -> State.merge2 state1 state2 (merge2Cells condition1 condition2)

    let internal mergeStates conditions states =
        State.merge conditions states mergeCells

    let internal guardedMap mapper gvs =
        let gs, vs = List.unzip gvs in
        vs |> List.map mapper |> List.zip gs |> merge

    let internal guardedStateMap mapper gvs state =
        let gs, vs = List.unzip gvs in
        let vs, states = vs |> List.map mapper |> List.unzip in
        vs |> List.zip gs |> merge, mergeStates gs states

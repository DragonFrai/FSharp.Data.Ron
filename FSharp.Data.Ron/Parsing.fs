module FSharp.Data.Ron.Parsing

open System
open FParsec

// https://github.com/ron-rs/ron/blob/995d9e93d98b93cfbf0d6040230f6732dd4cf32b/docs/grammar.md
module Grammar =

    open System.Text
    
    let valueR, valueRef = createParserForwardedToRef<RonValue, unit> ()
    
    let ident =
        let isAsciiIdStart c = isAsciiLetter c || c = '_'
        let isAsciiIdContinue c = isAsciiLetter c || isDigit c || c = '_'
        identifier (
            IdentifierOptions(
                isAsciiIdStart = isAsciiIdStart,
                isAsciiIdContinue = isAsciiIdContinue
            ))
    
    // ----------------
    // Whitespace and comments
    // ----------------
    
    // ws_single = "\n" | "\t" | "\r" | " ";
    let ws_single = anyOf " \n\t\r"
    
    // comment = ["//", { no_newline }, "\n"] | ["/*", { ? any character ? }, "*/"];
    let comment =
        let singleline = pstring "//" >>. manySatisfy (fun c -> c <> '\n') >>. pchar '\n'
        let multiline = between (pstring "/*") (pstring "*/") (charsTillString "*/" false Int32.MaxValue)
        (singleline >>% ()) <|> (multiline >>% ())
    
    // ws = { ws_single, comment };
    let ws = skipMany ((ws_single >>% ()) <|> comment)
    
    // ----------------
    // Commas
    // ----------------
    
    // comma = ws, ",", ws;
    let comma = ws >>. pchar ',' .>> ws
    
    // ----------------
    // Extensions
    // ----------------
    
    let extensions_name =
        // choice [ pstring "unwrap_newtypes"; pstring "implicit_some" ]
        ident
    
    // extensions_inner = "enable", ws, "(", extension_name, { comma, extension_name }, [comma], ws, ")";
    let extensions_inner =
        pstring "enable" >>. ws >>. pchar '(' >>. (sepEndBy1 (extensions_name .>> ws) comma) .>> ws .>> pchar ')'
    
    // extensions = { "#", ws, "!", ws, "[", ws, extensions_inner, ws, "]", ws };
    let extensions =
        many (pchar '#' >>. ws >>. pchar '!' >>. ws >>. pchar '[' >>. ws >>. extensions_inner .>> ws .>> pchar ']' .>> ws)
        |>> List.concat
    
    // ----------------
    // Numbers
    // ----------------
    
    // digit = "0" | "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9";
    let digit = satisfy isDigit
    
    // hex_digit = "A" | "a" | "B" | "b" | "C" | "c" | "D" | "d" | "E" | "e" | "F" | "f";
    let hex_digit = hex
    
    // unsigned = (["0", ("b" | "o")], digit, { digit | '_' } |
    //              "0x", (digit | hex_digit), { digit | hex_digit | '_' });
    let unsigned' =
        let binNumber = pstring "0b" .>>. many1Chars (anyOf "01") |>> fun(x,y)->x+y
        let octNumber = pstring "0o" .>>. many1Chars octal |>> fun(x,y)->x+y
        let hexNumber = pstring "0x" .>>. many1Chars hex |>> fun(x,y)->x+y
        let decNumber = many1Chars digit
        binNumber <|> octNumber <|> hexNumber <|> attempt decNumber
        <?> "Unsigned"
    
    let sign = anyOf "+-"
    
    // signed = ["+" | "-"], unsigned;
    let signed' =
        opt sign .>>.? unsigned'
        |>> fun (co0, s9) ->
            (match co0 with Some c -> string c | None -> "")  + s9
    
    // float_exp = ("e" | "E"), digit, {digit};
    let float_exp =
        anyOf "eE" .>>. many1 digit
        |>> fun (c0, cs) -> string c0 + String.Concat(cs)
    
    // float_std = ["+" | "-"], digit, { digit }, ".", {digit}, [float_exp];
    let float_std =
        opt sign .>>.? many1 digit .>>.? pchar '.' .>>. many digit .>>. opt float_exp
        |>> fun ((((c5o, c6s), c7), c8s), s9o) ->
            (match c5o with Some c -> string c | _ -> "") + String.Concat(c6s) + string c7 + String.Concat(c8s) + (Option.defaultValue "" s9o)
    
    // float_frac = ".", digit, {digit}, [float_exp];
    let float_frac =
        pchar '.' .>>. many1 digit .>>. opt float_exp
        |>> fun ((c0, c1s), s2o) -> string c0 + String.Join("", c1s) + (defaultArg s2o "")
    
    // float = float_std | float_frac;
    let floatP =
        float_std <|> float_frac
        |>> float
        <?> "Float"
    
    let floatR = floatP |>> RonValue.Float
    
    // ----------------
    // String
    // ----------------
    
    // string_raw_content = ("#", string_raw_content, "#") | "\"", { unicode_non_greedy }, "\"";
    let string_raw_content: Parser<string, _> =
        fun stream ->
            let mutable hashCount = 0
            while (stream.Peek() = '#') do
                stream.Read() |> ignore
                hashCount <- hashCount + 1
            let expectedHashes = String.replicate hashCount "#"
            if stream.Read() <> '"' then
                Reply(Error, expected "\"")
            else
                let sb = StringBuilder()
                let mutable doLoop = true
                while doLoop do
                    let c = stream.Read()
                    if c = '"' then
                        let hashes = stream.PeekString(hashCount)
                        if hashes = expectedHashes then
                            stream.Skip(hashCount)
                            doLoop <- false
                        else
                            sb.Append("\"") |> ignore
                    else
                        sb.Append(c) |> ignore
                let result = sb.ToString()
                Reply(result)
    
    // string_raw = "r" string_raw_content;
    let string_raw = pchar 'r' >>? string_raw_content
    
    // string_escape = "\\", ("\"" | "\\" | "b" | "f" | "n" | "r" | "t" | ("u", unicode_hex));
    let string_escape =
        let unicode_hex = many1Chars hex
        pchar '\\' .>>. ((anyOf [ '"'; '\\'; 'b'; 'f'; 'n'; 'r'; 't' ] |>> string) <|> ((pchar 'u' |>> string) .>>. unicode_hex |>> (fun (x, y) -> x + y)))
        |>> fun (c, s) -> string c + s
    
    // string_std = "\"", { no_double_quotation_marks | string_escape }, "\"";
    let string_std =
        let no_double_quotation_marks = noneOf ['"'] |>> string
        between (pchar '"') (pchar '"')
            (many (no_double_quotation_marks <|> string_escape))
        |>> fun ss -> String.Join("", ss)
    
    // string = string_std | string_raw;
    let string' =
        string_std <|> string_raw
    
    let stringR = string' |>> RonValue.String
    
    // ----------------
    // Char
    // ----------------
    
    // char = "'", (no_apostrophe | "\\\\" | "\\'"), "'";
    let char' =
        between (pchar '\'') (pchar '\'')
            ((noneOf ['''] |>> string) <|> pstring @"\\" <|> pstring @"\'" )
        |>> function @"\\" -> '\\' | @"\'" -> ''' | c -> char c
    
    let charR = char' |>> RonValue.Char
    
    // ----------------
    // Boolean
    // ----------------
    
    // bool = "true" | "false";
    let bool' =
        let true' = stringReturn "true" true
        let false' = stringReturn "false" false
        true' <|> false'
    
    let boolR = bool' |>> RonValue.Boolean
    
    // ----------------
    // Optional
    // ----------------
    
    // option = "Some", ws, "(", ws, value, ws, ")";
    let option' = pstring "Some" >>. ws >>. pchar '(' >>. ws >>. valueR .>> ws .>> pchar ')'
    
    // ----------------
    // List
    // ----------------
    
    // list = "[", [value, { comma, value }, [comma]], "]";
    let listP =
        between (pchar '[') (pchar ']')
            (ws >>. sepEndBy (valueR .>> ws) (comma >>. ws))
    
    let listR = listP |>> RonValue.List
    
    // ----------------
    // Map
    // ----------------
    
    // map_entry = value, ws, ":", ws, value;
    let map_entry = valueR .>> ws .>> pchar ':' .>> ws .>>. valueR
    
    // map = "{", [map_entry, { comma, map_entry }, [comma]], "}";
    let mapP =
        between (pchar '{') (pchar '}')
            (ws >>. sepEndBy (map_entry .>> ws) (comma >>. ws))
    
    let mapR = mapP |>> (Map.ofList >> RonValue.Map)
    
    // ----------------
    // AnyStruct
    // ----------------
    
    // named_field = ident, ws, ":", value;
    let named_field = ident .>> ws .>> pchar ':' .>> ws .>>. valueR
    
    let anyStruct =
        let unitContent = pchar '(' >>? ws >>? pchar ')' >>% ()
        let namedContent = pchar '(' >>? (ws >>? sepEndBy1 (named_field .>>? ws) comma) .>> pchar ')'
        let unnamedContent = pchar '(' >>? (ws >>? sepEndBy1 (valueR .>>? ws) comma) .>> pchar ')'
        
        let tagStruct = ident
        let unitStruct = opt ident .>>? unitContent
        let namedStruct = opt ident .>>.? namedContent
        let unnamedStruct = opt ident .>>.? unnamedContent
        choice [
            unitStruct |>> AnyStruct.Unit
            namedStruct |>> AnyStruct.Named
            unnamedStruct |>> AnyStruct.Unnamed
            tagStruct |>> AnyStruct.Tag
        ]
    
    let anyStructR = anyStruct |>> RonValue.AnyStruct
    
    // ----------------
    // Value
    // ----------------
    
    // value = unsigned | signed | float | string | char | bool | option | list | map | tuple | struct | enum_variant;
    do valueRef := choice [
        floatR
        unsigned' |>> (int >> RonValue.Integer)
        signed' |>> (int >> RonValue.Integer)
        stringR
        charR
        boolR
        option'
        anyStructR
        listR
        mapR
    ]
    
    // ----------------
    // RON file
    // ----------------
    
    // RON = [extensions], ws, value, ws;
    let RON =
        ws >>. opt extensions .>> ws .>>. valueR .>> ws .>> eof
        |>> function e, v -> (match e with None -> [] | Some es -> es), v
//        ws >>. valueR .>> ws .>> eof

type RonFile =
    { Extensions: string list
      Value: RonValue }

let parse input =
    match run Grammar.RON input with
    | Success ((exts, value), _, _) -> Result.Ok { Extensions = exts; Value = value }
    | Failure (errorMsg, _, _) -> Result.Error errorMsg

old user id that does not exists anymore server side is being regularly checked:

: Aria.Web.Hubs.ModelBridgeHub[0]
      [Bridge] Client connected: Fh2Q7KBlOgKyki5jfxLKQg
info: Aria.Web.Hubs.ModelBridgeHub[0]
      [Bridge/Direct] Challenge issued for userId=9 connId=Fh2Q7KBlOgKyki5jfxLKQg
warn: Aria.Web.Hubs.ModelBridgeHub[0]
      [Bridge/Direct] No public key on record for userId=9
info: Aria.Web.Hubs.ModelBridgeHub[0]
      [Bridge] Client disconnected: Fh2Q7KBlOgKyki5jfxLKQg reason=clean
fail: Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware[1]
      An unhandled exception has occurred while executing the request.
      Microsoft.AspNetCore.Http.BadHttpRequestException: Failed to read parameter "PendingEnrollRequest req" from the request body as JSON.
       ---> System.Text.Json.JsonException: The JSON value could not be converted to PendingEnrollRequest. Path: $.serverSoulId | LineNumber: 0 | BytePositionInLine: 17.
       ---> System.InvalidOperationException: Cannot get the value of a token type 'Number' as a string.
         at System.Text.Json.ThrowHelper.ThrowInvalidOperationException_ExpectedString(JsonTokenType tokenType)
         at System.Text.Json.Utf8JsonReader.GetString()
         at System.Text.Json.Serialization.JsonConverter`1.TryRead(Utf8JsonReader& reader, Type typeToConvert, JsonSerializerOptions options, ReadStack& state, T& value, Boolean& isPopulatedValue)
         at System.Text.Json.Serialization.JsonConverter`1.TryReadAsObject(Utf8JsonReader& reader, Type typeToConvert, JsonSerializerOptions options, ReadStack& state, Object& value)
         at System.Text.Json.Serialization.Converters.LargeObjectWithParameterizedConstructorConverter`1.ReadAndCacheConstructorArgument(ReadStack& state, Utf8JsonReader& reader, JsonParameterInfo jsonParameterInfo)
         at System.Text.Json.Serialization.Converters.ObjectWithParameterizedConstructorConverter`1.ReadConstructorArgumentsWithContinuation(ReadStack& state, Utf8JsonReader& reader, JsonSerializerOptions options)
         at System.Text.Json.Serialization.Converters.ObjectWithParameterizedConstructorConverter`1.OnTryRead(Utf8JsonReader& reader, Type typeToConvert, JsonSerializerOptions options, ReadStack& state, T& value)
         at System.Text.Json.Serialization.JsonConverter`1.ReadCore(Utf8JsonReader& reader, T& value, JsonSerializerOptions options, ReadStack& state)
         --- End of inner exception stack trace ---
         at System.Text.Json.ThrowHelper.ReThrowWithPath(ReadStack& state, Utf8JsonReader& reader, Exception ex)
         at System.Text.Json.Serialization.JsonConverter`1.ReadCore(Utf8JsonReader& reader, T& value, JsonSerializerOptions options, ReadStack& state)
         at System.Text.Json.Serialization.Metadata.JsonTypeInfo`1.ContinueDeserialize[TReadBufferState,TStream](TReadBufferState& bufferState, JsonReaderState& jsonReaderState, ReadStack& readStack, T& value)
         at System.Text.Json.Serialization.Metadata.JsonTypeInfo`1.DeserializeAsync[TReadBufferState,TStream](TStream utf8Json, TReadBufferState bufferState, CancellationToken cancellationToken)
         at System.Text.Json.Serialization.Metadata.JsonTypeInfo`1.DeserializeAsObjectAsync(PipeReader utf8Json, CancellationToken cancellationToken)
         at Microsoft.AspNetCore.Http.HttpRequestJsonExtensions.ReadFromJsonAsync(HttpRequest request, JsonTypeInfo jsonTypeInfo, CancellationToken cancellationToken)
         at Microsoft.AspNetCore.Http.HttpRequestJsonExtensions.ReadFromJsonAsync(HttpRequest request, JsonTypeInfo jsonTypeInfo, CancellationToken cancellationToken)
         at Microsoft.AspNetCore.Http.RequestDelegateFactory.<HandleRequestBodyAndCompileRequestDelegateForJson>g__TryReadBodyAsync|102_0(HttpContext httpContext, Type bodyType, String parameterTypeName, String parameterName, Boolean allowEmptyRequestBody, Boolean throwOnBadRequest, JsonTypeInfo jsonTypeInfo)
         --- End of inner exception stack trace ---
         at Microsoft.AspNetCore.Http.RequestDelegateFactory.Log.InvalidJsonRequestBody(HttpContext httpContext, String parameterTypeName, String parameterName, Exception exception, Boolean shouldThrow)
         at Microsoft.AspNetCore.Http.RequestDelegateFactory.<HandleRequestBodyAndCompileRequestDelegateForJson>g__TryReadBodyAsync|102_0(HttpContext httpContext, Type bodyType, String parameterTypeName, String parameterName, Boolean allowEmptyRequestBody, Boolean throwOnBadRequest, JsonTypeInfo jsonTypeInfo)
         at Microsoft.AspNetCore.Http.RequestDelegateFactory.<>c__DisplayClass102_2.<<HandleRequestBodyAndCompileRequestDelegateForJson>b__2>d.MoveNext()
      --- End of stack trace from previous location ---
         at Microsoft.AspNetCore.Diagnostics.StatusCodePagesMiddleware.Invoke(HttpContext context)
         at Microsoft.AspNetCore.Authorization.AuthorizationMiddleware.Invoke(HttpContext context)
         at Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddlewareImpl.Invoke(HttpContext context)
info:
/* eslint-disable */
// AUTO-GENERATED from docs/contracts/openapi.json by scripts/generate-contracts.mjs
// Do not edit manually.
export type paths = {
    "/api/gateway/approvals/{approvalId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    approvalId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["SubmitExecutionApprovalRequest"];
                    "application/json": components["schemas"]["SubmitExecutionApprovalRequest"];
                    "text/json": components["schemas"]["SubmitExecutionApprovalRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/approvals/{approvalId}/script": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    approvalId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PendingApprovalScriptSnapshot"];
                        "text/json": components["schemas"]["PendingApprovalScriptSnapshot"];
                        "text/plain": components["schemas"]["PendingApprovalScriptSnapshot"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/artifacts/verify": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["VerifyForgeArtifactRequest"];
                    "application/json": components["schemas"]["VerifyForgeArtifactRequest"];
                    "text/json": components["schemas"]["VerifyForgeArtifactRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/chat": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["GatewayChatRequest"];
                    "application/json": components["schemas"]["GatewayChatRequest"];
                    "text/json": components["schemas"]["GatewayChatRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/conversations/{conversationId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    conversationId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeleteResourceResponse"];
                        "text/json": components["schemas"]["DeleteResourceResponse"];
                        "text/plain": components["schemas"]["DeleteResourceResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        options?: never;
        head?: never;
        patch: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    conversationId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateConversationRequest"];
                    "application/json": components["schemas"]["UpdateConversationRequest"];
                    "text/json": components["schemas"]["UpdateConversationRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ConversationSummaryDto"];
                        "text/json": components["schemas"]["ConversationSummaryDto"];
                        "text/plain": components["schemas"]["ConversationSummaryDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        trace?: never;
    };
    "/api/gateway/history/{conversationId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    conversationId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: {
                    name?: string;
                    workMode?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeleteResourceResponse"];
                        "text/json": components["schemas"]["DeleteResourceResponse"];
                        "text/plain": components["schemas"]["DeleteResourceResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        options?: never;
        head?: never;
        patch: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateSessionRequest"];
                    "application/json": components["schemas"]["UpdateSessionRequest"];
                    "text/json": components["schemas"]["UpdateSessionRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SessionSummaryDto"];
                        "text/json": components["schemas"]["SessionSummaryDto"];
                        "text/plain": components["schemas"]["SessionSummaryDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/approvals": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PendingApprovalsResponse"];
                        "text/json": components["schemas"]["PendingApprovalsResponse"];
                        "text/plain": components["schemas"]["PendingApprovalsResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/attachments": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "multipart/form-data": {
                        file?: components["schemas"]["IFormFile"];
                    };
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["UploadedAttachmentDto"];
                        "text/json": components["schemas"]["UploadedAttachmentDto"];
                        "text/plain": components["schemas"]["UploadedAttachmentDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/browser": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserSessionStateDto"];
                        "text/json": components["schemas"]["BrowserSessionStateDto"];
                        "text/plain": components["schemas"]["BrowserSessionStateDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/browser/close": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserSessionCloseResponse"];
                        "text/json": components["schemas"]["BrowserSessionCloseResponse"];
                        "text/plain": components["schemas"]["BrowserSessionCloseResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/browser/screenshot": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description No Content */
                204: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Modified */
                304: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/browser/takeover/complete": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["BrowserTakeoverCompleteRequest"];
                    "application/json": null | components["schemas"]["BrowserTakeoverCompleteRequest"];
                    "text/json": null | components["schemas"]["BrowserTakeoverCompleteRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserTakeoverResponse"];
                        "text/json": components["schemas"]["BrowserTakeoverResponse"];
                        "text/plain": components["schemas"]["BrowserTakeoverResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/browser/takeover/start": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["BrowserTakeoverStartRequest"];
                    "application/json": null | components["schemas"]["BrowserTakeoverStartRequest"];
                    "text/json": null | components["schemas"]["BrowserTakeoverStartRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserTakeoverResponse"];
                        "text/json": components["schemas"]["BrowserTakeoverResponse"];
                        "text/plain": components["schemas"]["BrowserTakeoverResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/cancel": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CancelTurnResponse"];
                        "text/json": components["schemas"]["CancelTurnResponse"];
                        "text/plain": components["schemas"]["CancelTurnResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/changes": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WorkspaceChangesDto"];
                        "text/json": components["schemas"]["WorkspaceChangesDto"];
                        "text/plain": components["schemas"]["WorkspaceChangesDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/compact": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["CompactSessionContextRequest"];
                    "application/json": null | components["schemas"]["CompactSessionContextRequest"];
                    "text/json": null | components["schemas"]["CompactSessionContextRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CompactSessionContextResponse"];
                        "text/json": components["schemas"]["CompactSessionContextResponse"];
                        "text/plain": components["schemas"]["CompactSessionContextResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/context-window": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/conversations": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    includeArchived?: boolean;
                    page?: number | string;
                    pageSize?: number | string;
                };
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PagedResponseDtoOfConversationSummaryDto"];
                        "text/json": components["schemas"]["PagedResponseDtoOfConversationSummaryDto"];
                        "text/plain": components["schemas"]["PagedResponseDtoOfConversationSummaryDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["CreateConversationRequest"];
                    "application/json": null | components["schemas"]["CreateConversationRequest"];
                    "text/json": null | components["schemas"]["CreateConversationRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ConversationSummaryDto"];
                        "text/json": components["schemas"]["ConversationSummaryDto"];
                        "text/plain": components["schemas"]["ConversationSummaryDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/files": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    path?: string;
                };
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WorkspaceDirectoryListingDto"];
                        "text/json": components["schemas"]["WorkspaceDirectoryListingDto"];
                        "text/plain": components["schemas"]["WorkspaceDirectoryListingDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/files/content": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    path?: string;
                };
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["WorkspaceFileContentDto"];
                        "text/json": components["schemas"]["WorkspaceFileContentDto"];
                        "text/plain": components["schemas"]["WorkspaceFileContentDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/plan": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SessionPlanDto"];
                        "text/json": components["schemas"]["SessionPlanDto"];
                        "text/plain": components["schemas"]["SessionPlanDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/plan-mode": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdatePlanModeRequest"];
                    "application/json": components["schemas"]["UpdatePlanModeRequest"];
                    "text/json": components["schemas"]["UpdatePlanModeRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PlanModeResponse"];
                        "text/json": components["schemas"]["PlanModeResponse"];
                        "text/plain": components["schemas"]["PlanModeResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/snapshot": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    conversationId?: string;
                };
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SessionSnapshotDto"];
                        "text/json": components["schemas"]["SessionSnapshotDto"];
                        "text/plain": components["schemas"]["SessionSnapshotDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/{sessionId}/tasklist": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/bootstrap": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["BootstrapRequest"];
                    "application/json": null | components["schemas"]["BootstrapRequest"];
                    "text/json": null | components["schemas"]["BootstrapRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/session/daily-assistant": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SessionBootstrapResponse"];
                        "text/json": components["schemas"]["SessionBootstrapResponse"];
                        "text/plain": components["schemas"]["SessionBootstrapResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/sessions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    includeArchived?: boolean;
                    page?: number | string;
                    pageSize?: number | string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PagedResponseDtoOfSessionSummaryDto"];
                        "text/json": components["schemas"]["PagedResponseDtoOfSessionSummaryDto"];
                        "text/plain": components["schemas"]["PagedResponseDtoOfSessionSummaryDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/sse": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    epoch?: string;
                    lastEventId?: number | string;
                    sessionId?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/status": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    sessionId?: string;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/gateway/workspaces": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["OpenLocalWorkspaceRequest"];
                    "application/json": components["schemas"]["OpenLocalWorkspaceRequest"];
                    "text/json": components["schemas"]["OpenLocalWorkspaceRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["OpenLocalWorkspaceResponse"];
                        "text/json": components["schemas"]["OpenLocalWorkspaceResponse"];
                        "text/plain": components["schemas"]["OpenLocalWorkspaceResponse"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GatewayErrorResponse"];
                        "text/json": components["schemas"]["GatewayErrorResponse"];
                        "text/plain": components["schemas"]["GatewayErrorResponse"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["CreateBackgroundJobRequest"];
                    "application/json": components["schemas"]["CreateBackgroundJobRequest"];
                    "text/json": components["schemas"]["CreateBackgroundJobRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}/cancel": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}/continue": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["ContinueWorkerHttpRequest"];
                    "application/json": null | components["schemas"]["ContinueWorkerHttpRequest"];
                    "text/json": null | components["schemas"]["ContinueWorkerHttpRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    afterSeq?: number | string;
                };
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}/resume": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["ResumeJobRequest"];
                    "application/json": null | components["schemas"]["ResumeJobRequest"];
                    "text/json": null | components["schemas"]["ResumeJobRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}/retry": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/{jobId}/status": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    jobId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/my/cancel-all": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/session/{sessionId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/jobs/session/{sessionId}/cancel-all": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    sessionId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content?: never;
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugin-operations/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PluginOperationStatusDto"];
                        "text/json": components["schemas"]["PluginOperationStatusDto"];
                        "text/plain": components["schemas"]["PluginOperationStatusDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessPluginDto"][];
                        "text/json": components["schemas"]["BusinessPluginDto"][];
                        "text/plain": components["schemas"]["BusinessPluginDto"][];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["CreateBusinessPluginRequest"];
                    "application/json": components["schemas"]["CreateBusinessPluginRequest"];
                    "text/json": components["schemas"]["CreateBusinessPluginRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessPluginDto"];
                        "text/json": components["schemas"]["BusinessPluginDto"];
                        "text/plain": components["schemas"]["BusinessPluginDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/capabilities": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    executableOnly?: boolean;
                    groupTag?: string;
                    page?: number | string;
                    pageSize?: number | string;
                    search?: string;
                    snapshotId?: string;
                };
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CapabilityPageResponseDto"];
                        "text/json": components["schemas"]["CapabilityPageResponseDto"];
                        "text/plain": components["schemas"]["CapabilityPageResponseDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/capabilities/detail": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    capabilityId?: string;
                    snapshotId?: string;
                };
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CapabilityDetailDto"];
                        "text/json": components["schemas"]["CapabilityDetailDto"];
                        "text/plain": components["schemas"]["CapabilityDetailDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/capabilities/risk": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateCapabilityRiskRequest"];
                    "application/json": components["schemas"]["UpdateCapabilityRiskRequest"];
                    "text/json": components["schemas"]["UpdateCapabilityRiskRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CapabilitySummaryDto"];
                        "text/json": components["schemas"]["CapabilitySummaryDto"];
                        "text/plain": components["schemas"]["CapabilitySummaryDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/connection": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdatePluginConnectionRequest"];
                    "application/json": components["schemas"]["UpdatePluginConnectionRequest"];
                    "text/json": components["schemas"]["UpdatePluginConnectionRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessPluginDto"];
                        "text/json": components["schemas"]["BusinessPluginDto"];
                        "text/plain": components["schemas"]["BusinessPluginDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/credentials/{slot}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                    slot: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["SetPluginCredentialRequest"];
                    "application/json": components["schemas"]["SetPluginCredentialRequest"];
                    "text/json": components["schemas"]["SetPluginCredentialRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["CredentialRefSummaryDto"];
                        "text/json": components["schemas"]["CredentialRefSummaryDto"];
                        "text/plain": components["schemas"]["CredentialRefSummaryDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete: {
            parameters: {
                query?: {
                    unbindIfReferenced?: boolean;
                };
                header?: never;
                path: {
                    id: string;
                    slot: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeleteCredentialResponseDto"];
                        "text/json": components["schemas"]["DeleteCredentialResponseDto"];
                        "text/plain": components["schemas"]["DeleteCredentialResponseDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeleteCredentialResponseDto"];
                        "text/json": components["schemas"]["DeleteCredentialResponseDto"];
                        "text/plain": components["schemas"]["DeleteCredentialResponseDto"];
                    };
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/disable": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DisablePluginResponseDto"];
                        "text/json": components["schemas"]["DisablePluginResponseDto"];
                        "text/plain": components["schemas"]["DisablePluginResponseDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/{id}/discover": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["DiscoverPluginRequest"];
                    "application/json": components["schemas"]["DiscoverPluginRequest"];
                    "text/json": components["schemas"]["DiscoverPluginRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DiscoverPluginResultDto"];
                        "text/json": components["schemas"]["DiscoverPluginResultDto"];
                        "text/plain": components["schemas"]["DiscoverPluginResultDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/plugins/maintenance/prune": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: {
                content: {
                    "application/*+json": null | components["schemas"]["BusinessSkillRetentionPruneRequest"];
                    "application/json": null | components["schemas"]["BusinessSkillRetentionPruneRequest"];
                    "text/json": null | components["schemas"]["BusinessSkillRetentionPruneRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillRetentionPruneResultDto"];
                        "text/json": components["schemas"]["BusinessSkillRetentionPruneResultDto"];
                        "text/plain": components["schemas"]["BusinessSkillRetentionPruneResultDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/settings/browser": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserSettingsDto"];
                        "text/json": components["schemas"]["BrowserSettingsDto"];
                        "text/plain": components["schemas"]["BrowserSettingsDto"];
                    };
                };
            };
        };
        put: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateBrowserSettingsRequest"];
                    "application/json": components["schemas"]["UpdateBrowserSettingsRequest"];
                    "text/json": components["schemas"]["UpdateBrowserSettingsRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserSettingsDto"];
                        "text/json": components["schemas"]["BrowserSettingsDto"];
                        "text/plain": components["schemas"]["BrowserSettingsDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/json": components["schemas"]["BrowserApiErrorDto"];
                        "text/plain": components["schemas"]["BrowserApiErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/settings/execution": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ExecutionSettingsDto"];
                        "text/json": components["schemas"]["ExecutionSettingsDto"];
                        "text/plain": components["schemas"]["ExecutionSettingsDto"];
                    };
                };
            };
        };
        put: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateExecutionSettingsRequest"];
                    "application/json": components["schemas"]["UpdateExecutionSettingsRequest"];
                    "text/json": components["schemas"]["UpdateExecutionSettingsRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ExecutionSettingsDto"];
                        "text/json": components["schemas"]["ExecutionSettingsDto"];
                        "text/plain": components["schemas"]["ExecutionSettingsDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/settings/model": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ModelSettingsDto"];
                        "text/json": components["schemas"]["ModelSettingsDto"];
                        "text/plain": components["schemas"]["ModelSettingsDto"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SettingsErrorDto"];
                        "text/json": components["schemas"]["SettingsErrorDto"];
                        "text/plain": components["schemas"]["SettingsErrorDto"];
                    };
                };
            };
        };
        put: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateModelSettingsRequest"];
                    "application/json": components["schemas"]["UpdateModelSettingsRequest"];
                    "text/json": components["schemas"]["UpdateModelSettingsRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ModelSettingsDto"];
                        "text/json": components["schemas"]["ModelSettingsDto"];
                        "text/plain": components["schemas"]["ModelSettingsDto"];
                    };
                };
                /** @description Bad Request */
                400: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SettingsErrorDto"];
                        "text/json": components["schemas"]["SettingsErrorDto"];
                        "text/plain": components["schemas"]["SettingsErrorDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SettingsErrorDto"];
                        "text/json": components["schemas"]["SettingsErrorDto"];
                        "text/plain": components["schemas"]["SettingsErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/settings/permissions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PermissionRuleListResponse"];
                        "text/json": components["schemas"]["PermissionRuleListResponse"];
                        "text/plain": components["schemas"]["PermissionRuleListResponse"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/settings/permissions/{ruleId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    ruleId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["DeletePermissionRuleResponse"];
                        "text/json": components["schemas"]["DeletePermissionRuleResponse"];
                        "text/plain": components["schemas"]["DeletePermissionRuleResponse"];
                    };
                };
            };
        };
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["CreateSkillDraftRequest"];
                    "application/json": components["schemas"]["CreateSkillDraftRequest"];
                    "text/json": components["schemas"]["CreateSkillDraftRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillDraftDto"];
                        "text/json": components["schemas"]["SkillDraftDto"];
                        "text/plain": components["schemas"]["SkillDraftDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillDraftDto"];
                        "text/json": components["schemas"]["SkillDraftDto"];
                        "text/plain": components["schemas"]["SkillDraftDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["UpdateSkillDraftRequest"];
                    "application/json": components["schemas"]["UpdateSkillDraftRequest"];
                    "text/json": components["schemas"]["UpdateSkillDraftRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillDraftDto"];
                        "text/json": components["schemas"]["SkillDraftDto"];
                        "text/plain": components["schemas"]["SkillDraftDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts/{id}/generate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["GenerateSkillCandidateRequest"];
                    "application/json": components["schemas"]["GenerateSkillCandidateRequest"];
                    "text/json": components["schemas"]["GenerateSkillCandidateRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["GenerateSkillCandidateResultDto"];
                        "text/json": components["schemas"]["GenerateSkillCandidateResultDto"];
                        "text/plain": components["schemas"]["GenerateSkillCandidateResultDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Service Unavailable */
                503: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts/{id}/publish": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["PublishSkillDraftRequest"];
                    "application/json": components["schemas"]["PublishSkillDraftRequest"];
                    "text/json": components["schemas"]["PublishSkillDraftRequest"];
                };
            };
            responses: {
                /** @description Created */
                201: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PublishedSkillDto"];
                        "text/json": components["schemas"]["PublishedSkillDto"];
                        "text/plain": components["schemas"]["PublishedSkillDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts/{id}/selection": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["PutDraftSelectionRequest"];
                    "application/json": components["schemas"]["PutDraftSelectionRequest"];
                    "text/json": components["schemas"]["PutDraftSelectionRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillDraftDto"];
                        "text/json": components["schemas"]["SkillDraftDto"];
                        "text/plain": components["schemas"]["SkillDraftDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts/{id}/test-runs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["StartSkillRunRequest"];
                    "application/json": components["schemas"]["StartSkillRunRequest"];
                    "text/json": components["schemas"]["StartSkillRunRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillRunDto"];
                        "text/json": components["schemas"]["SkillRunDto"];
                        "text/plain": components["schemas"]["SkillRunDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-drafts/{id}/validate": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillDraftDto"];
                        "text/json": components["schemas"]["SkillDraftDto"];
                        "text/plain": components["schemas"]["SkillDraftDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-runs/{runId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    runId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillRunDto"];
                        "text/json": components["schemas"]["SkillRunDto"];
                        "text/plain": components["schemas"]["SkillRunDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skill-runs/{runId}/cancel": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    runId: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillRunDto"];
                        "text/json": components["schemas"]["SkillRunDto"];
                        "text/plain": components["schemas"]["SkillRunDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skills": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PublishedSkillDto"][];
                        "text/json": components["schemas"]["PublishedSkillDto"][];
                        "text/plain": components["schemas"]["PublishedSkillDto"][];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skills/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PublishedSkillDto"];
                        "text/json": components["schemas"]["PublishedSkillDto"];
                        "text/plain": components["schemas"]["PublishedSkillDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skills/{id}/activate-version": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["ActivateSkillVersionRequest"];
                    "application/json": components["schemas"]["ActivateSkillVersionRequest"];
                    "text/json": components["schemas"]["ActivateSkillVersionRequest"];
                };
            };
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["PublishedSkillDto"];
                        "text/json": components["schemas"]["PublishedSkillDto"];
                        "text/plain": components["schemas"]["PublishedSkillDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skills/{id}/markdown": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    version?: number | string;
                };
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillMarkdownPreviewDto"];
                        "text/json": components["schemas"]["SkillMarkdownPreviewDto"];
                        "text/plain": components["schemas"]["SkillMarkdownPreviewDto"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/skills/{id}/runs": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillRunDto"][];
                        "text/json": components["schemas"]["SkillRunDto"][];
                        "text/plain": components["schemas"]["SkillRunDto"][];
                    };
                };
            };
        };
        put?: never;
        post: {
            parameters: {
                query?: never;
                header?: never;
                path: {
                    id: string;
                };
                cookie?: never;
            };
            requestBody: {
                content: {
                    "application/*+json": components["schemas"]["StartSkillRunRequest"];
                    "application/json": components["schemas"]["StartSkillRunRequest"];
                    "text/json": components["schemas"]["StartSkillRunRequest"];
                };
            };
            responses: {
                /** @description Accepted */
                202: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SkillRunDto"];
                        "text/json": components["schemas"]["SkillRunDto"];
                        "text/plain": components["schemas"]["SkillRunDto"];
                    };
                };
                /** @description Forbidden */
                403: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["ProblemDetails"];
                        "text/json": components["schemas"]["ProblemDetails"];
                        "text/plain": components["schemas"]["ProblemDetails"];
                    };
                };
                /** @description Not Found */
                404: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Conflict */
                409: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
                /** @description Unprocessable Entity */
                422: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/json": components["schemas"]["BusinessSkillErrorDto"];
                        "text/plain": components["schemas"]["BusinessSkillErrorDto"];
                    };
                };
            };
        };
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/system/status": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: {
            parameters: {
                query?: {
                    refresh?: boolean;
                };
                header?: never;
                path?: never;
                cookie?: never;
            };
            requestBody?: never;
            responses: {
                /** @description OK */
                200: {
                    headers: {
                        [name: string]: unknown;
                    };
                    content: {
                        "application/json": components["schemas"]["SystemStatusSummaryDto"];
                        "text/json": components["schemas"]["SystemStatusSummaryDto"];
                        "text/plain": components["schemas"]["SystemStatusSummaryDto"];
                    };
                };
            };
        };
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
};
export type webhooks = Record<string, never>;
export type components = {
    schemas: {
        ActivateSkillVersionRequest: {
            /** Format: int32 */
            version: number | string;
        };
        BackgroundJobDto: {
            failure?: null | components["schemas"]["BackgroundJobFailureDto"];
            jobId?: string;
            jobType?: string;
            latestMessage?: null | string;
            /** Format: int64 */
            latestSeq?: number | string;
            /** Format: double */
            progress?: number | string;
            recoveryNeeded?: boolean;
            recoveryReason?: null | string;
            resumeGuidance?: null | string;
            resumeStrategy?: null | string;
            resumeToken?: null | string;
            runnerId?: null | string;
            sessionId?: null | string;
            stateKind?: null | string;
            status?: string;
            summary?: null | string;
            taskId?: null | string;
            /** Format: date-time */
            updatedAtUtc?: string;
            verification?: null | components["schemas"]["BackgroundJobVerificationDto"];
            waitingReason?: null | string;
            waitingUser?: boolean;
            workerType?: null | string;
        };
        BackgroundJobFailureDto: {
            code?: string;
            evidence?: string[];
            kind?: string;
            message?: string;
            refusedPaths?: string[];
            remedy?: string;
            retryable?: boolean;
        };
        BackgroundJobVerificationChecklistDto: {
            completedItems?: components["schemas"]["BackgroundJobVerificationChecklistItemDto"][];
            failedItems?: components["schemas"]["BackgroundJobVerificationChecklistItemDto"][];
            focusItems?: string[];
            pendingItems?: components["schemas"]["BackgroundJobVerificationChecklistItemDto"][];
        };
        BackgroundJobVerificationChecklistItemDto: {
            check?: string;
            evidence?: string[];
        };
        BackgroundJobVerificationDto: {
            checklist?: null | components["schemas"]["BackgroundJobVerificationChecklistDto"];
            evidence?: components["schemas"]["BackgroundJobVerificationEvidenceDto"][];
            issues?: string[];
            passed?: boolean;
            summary?: null | string;
            verdict?: string;
        };
        BackgroundJobVerificationEvidenceDto: {
            check?: string;
            command?: null | string;
            exitCode?: null | string;
            observation?: null | string;
            passed?: boolean;
        };
        BootstrapRequest: {
            targetSessionId?: null | string;
        };
        BrowserActionRecordDto: {
            action: string;
            /** Format: int64 */
            durationMs: number | string;
            /** Format: int64 */
            seq: number | string;
            succeeded: boolean;
            summary: null | string;
            target: null | string;
            /** Format: date-time */
            timestampUtc: string;
            tool: string;
            url: null | string;
        };
        BrowserApiErrorDto: {
            code: string;
            message: string;
        };
        BrowserApprovalRequestDto: {
            action: string;
            allowedDecisions: string[];
            approvalId: string;
            controlPlaneAvailable: boolean;
            /** Format: date-time */
            expiresAtUtc: string;
            expressionOrPath: null | string;
            requestDigest: string;
            riskReason: string;
            selfApprovalProtection: boolean;
            sessionId: string;
            targetDescription: string;
            targetOrigin: string;
            targetUrl: string;
            tool: string;
        };
        BrowserConsoleEntryDto: {
            level: string;
            /** Format: int32 */
            lineNumber: null | number | string;
            sourceUrl: null | string;
            text: string;
            /** Format: date-time */
            timestampUtc: string;
        };
        BrowserExecutableStatusDto: {
            available: boolean;
            browserName: null | string;
            diagnosticMessage?: null | string;
            executablePath: null | string;
            source: null | string;
            version: null | string;
        };
        BrowserSessionCloseResponse: {
            closed: boolean;
            sessionId: string;
        };
        BrowserSessionStateDto: {
            /** Format: int32 */
            consoleErrorCount: number | string;
            headless: boolean;
            /** @default false */
            isRemote: boolean;
            profileMode: string;
            recentActions: components["schemas"]["BrowserActionRecordDto"][];
            recentConsole: components["schemas"]["BrowserConsoleEntryDto"][];
            /** Format: int64 */
            screenshotSeq: number | string;
            sessionId: string;
            status: string;
            tabs: components["schemas"]["BrowserTabDto"][];
            /** @default false */
            takeoverActive: boolean;
            takeoverReason?: null | string;
            /** Format: date-time */
            takeoverStartedAtUtc?: null | string;
            title: null | string;
            /** Format: date-time */
            updatedAtUtc?: null | string;
            url: null | string;
        };
        BrowserSettingsDto: {
            /** @default false */
            allowControlledDownloads: boolean;
            allowedDomains: string[];
            allowLocalhostInDailyMode: boolean;
            blockedDomains: string[];
            detectedBrowser: components["schemas"]["BrowserExecutableStatusDto"];
            developmentAllowedLoopbackPorts: (number | string)[];
            enabled: boolean;
            executablePath: null | string;
            headless: boolean;
            /** Format: int32 */
            idleTimeoutMinutes: number | string;
            interactionApprovalPolicy: string;
            /** @default false */
            isRemoteCdpConfigured: boolean;
            /** Format: int32 */
            maxConcurrentSessions: number | string;
            profileMode: string;
            remoteCdpWsUrl?: null | string;
        };
        BrowserTabDto: {
            active: boolean;
            tabId: string;
            title: string;
            url: string;
        };
        BrowserTakeoverCompleteRequest: {
            note?: null | string;
        };
        BrowserTakeoverResponse: {
            active: boolean;
            message?: null | string;
            sessionId: string;
        };
        BrowserTakeoverStartRequest: {
            reason?: null | string;
        };
        BusinessApprovalRequestDto: {
            allowedDecisions?: string[];
            approvalId?: string;
            /** Format: date-time */
            createdAtUtc?: string;
            /** Format: date-time */
            expiresAtUtc?: string;
            kind?: string;
            requestDigest?: string;
            runId?: string;
            sessionId?: string;
            stepId?: string;
            summary?: components["schemas"]["BusinessApprovalSummaryDto"];
        };
        BusinessApprovalRequestPreviewDto: {
            capabilityId: string;
            methodOrTool: string;
            redactedBodyPreview: string;
            targetPath: string;
        };
        BusinessApprovalSummaryDto: {
            /** Format: int32 */
            batchCount?: number | string;
            currency?: null | string;
            environment?: string;
            /** Format: double */
            keyAmount?: null | number | string;
            plannedMutation?: string;
            requestPreviews?: components["schemas"]["BusinessApprovalRequestPreviewDto"][];
            systemName?: string;
            targetRecordSummary?: string;
            writeStepUnverified?: boolean;
        };
        /** @enum {unknown} */
        BusinessAuthKind: "None" | "ApiKey" | "Bearer" | "BasicTlsOnly" | "OAuth2AuthCodePkce";
        BusinessPluginDto: {
            activeSnapshotId?: null | string;
            /** Format: int32 */
            capabilityCount?: number | string;
            connection?: null | components["schemas"]["PluginConnectionDto"];
            connectionId?: string;
            enabled?: boolean;
            id?: string;
            /** Format: date-time */
            lastDiscoveredAtUtc?: null | string;
            name?: string;
            sourceType?: components["schemas"]["BusinessPluginSourceType"];
        };
        /** @enum {unknown} */
        BusinessPluginSourceType: "OpenApi" | "Mcp";
        BusinessSkillErrorDto: {
            code: string;
            correlationId: string;
            fieldErrors: components["schemas"]["BusinessSkillFieldError"][];
            message: string;
            retryable: boolean;
        };
        BusinessSkillFieldError: {
            code: string;
            field: string;
            message: string;
        };
        BusinessSkillRetentionPruneRequest: {
            /** Format: int32 */
            diagnosticsRetentionDays?: null | number | string;
            /** Format: int32 */
            operationHistoryRetentionDays?: null | number | string;
            /** Format: int32 */
            runHistoryRetentionDays?: null | number | string;
        };
        BusinessSkillRetentionPruneResultDto: {
            /** Format: int32 */
            deletedOperationsCount: number | string;
            /** Format: int32 */
            deletedOrphanSnapshotsCount: number | string;
            /** Format: int32 */
            deletedTerminalRunsCount: number | string;
            /** Format: date-time */
            executedAtUtc: string;
            /** Format: int32 */
            retainedOutcomeUnknownRunsCount: number | string;
            /** Format: int32 */
            retainedProtectedSnapshotsCount: number | string;
            /** Format: int32 */
            trimmedStepDiagnosticsCount: number | string;
        };
        CancelTurnResponse: {
            cancelled: boolean;
            sessionId: string;
            status: string;
        };
        CapabilityDetailDto: {
            inputSchemaJson?: null | string;
            invocation?: components["schemas"]["CapabilityInvocationSpecDto"];
            outputSchemaJson?: null | string;
            summary?: components["schemas"]["CapabilitySummaryDto"];
        };
        /** @enum {unknown} */
        CapabilityExecutabilityStatus: "Executable" | "NeedsConfiguration" | "Unsupported";
        CapabilityInvocationSpecDto: {
            authSchemeRefs?: string[];
            httpMethod?: null | string;
            mcpToolName?: null | string;
            normalizedPath?: null | string;
            parameters?: components["schemas"]["CapabilityParameterSpecDto"][];
            requestBodySchemaJson?: null | string;
            requestContentType?: null | string;
            responseContentType?: null | string;
            responseSchemaJson?: null | string;
            securityAlternatives?: components["schemas"]["CapabilitySecurityRequirementDto"][];
            selectedServerUrl?: null | string;
            serverVariableAllowedValues?: {
                [key: string]: string[];
            };
            sourceType?: components["schemas"]["BusinessPluginSourceType"];
        };
        CapabilityPageResponseDto: {
            complete: boolean;
            /** Format: int32 */
            executableCount: number | string;
            /** Format: int32 */
            filteredCount: number | string;
            items: components["schemas"]["CapabilitySummaryDto"][];
            /** Format: int32 */
            needsConfigurationCount: number | string;
            /** Format: int32 */
            page: number | string;
            /** Format: int32 */
            pageSize: number | string;
            pluginId: string;
            snapshotId: string;
            /** Format: int32 */
            totalCount: number | string;
            /** Format: int32 */
            unsupportedCount: number | string;
        };
        CapabilityParameterSpecDto: {
            description?: null | string;
            explode?: null | boolean;
            location?: string;
            name?: string;
            required?: boolean;
            schemaType?: string;
            style?: null | string;
        };
        /** @enum {unknown} */
        CapabilityRiskLevel: "Unknown" | "Read" | "Write" | "Destructive";
        CapabilitySecurityRequirementDto: {
            schemes: components["schemas"]["CapabilitySecuritySchemeRequirementDto"][];
        };
        CapabilitySecuritySchemeRequirementDto: {
            headerOrParamName: null | string;
            httpScheme: null | string;
            inLocation: null | string;
            schemeId: string;
            schemeType: string;
            scopes: string[];
        };
        CapabilitySummaryDto: {
            description?: string;
            diagnostics?: string[];
            executability?: components["schemas"]["CapabilityExecutabilityStatus"];
            groupTag?: string;
            id?: string;
            modelAlias?: string;
            nativeIdentity?: string;
            risk?: components["schemas"]["CapabilityRiskLevel"];
            snapshotId?: string;
            sourceDescriptionMissing?: boolean;
            suggestedSourceRiskHint?: null | components["schemas"]["CapabilityRiskLevel"];
            title?: string;
        };
        /** @enum {unknown} */
        CatalogDiffChangeKind: "Added" | "Removed" | "InputChanged" | "OutputChanged" | "TargetOrAuthChanged" | "DescriptionChanged";
        CatalogDiffDto: {
            baseSnapshotId: string;
            entries: components["schemas"]["CatalogDiffEntryDto"][];
            targetSnapshotId: string;
            triggersNeedsRevalidation: boolean;
        };
        CatalogDiffEntryDto: {
            capabilityId: string;
            changeKind: components["schemas"]["CatalogDiffChangeKind"];
            requiresRevalidation: boolean;
            summary: string;
            title: string;
        };
        CatalogSnapshotDto: {
            complete?: boolean;
            diagnostics?: string[];
            /** Format: int32 */
            executableCount?: number | string;
            /** Format: date-time */
            fetchedAtUtc?: string;
            id?: string;
            /** Format: int32 */
            needsConfigurationCount?: number | string;
            pluginId?: string;
            protocolVersion?: string;
            schemaHash?: string;
            sourceHash?: string;
            /** Format: int32 */
            totalCapabilities?: number | string;
            /** Format: int32 */
            unsupportedCount?: number | string;
        };
        ChatAttachmentDto: {
            contentType?: null | string;
            /** @default false */
            isWorkspaceReference: boolean;
            name: string;
            relativePath: string;
            /** Format: int64 */
            sizeBytes: number | string;
        };
        CompactSessionContextRequest: {
            conversationId?: null | string;
            focus?: null | string;
        };
        CompactSessionContextResponse: {
            compressed?: boolean;
            /** Format: int32 */
            compressedTurnCount?: number | string;
            contextWindow?: components["schemas"]["SessionContextWindowResponse"];
            /** Format: int64 */
            estimatedTokensAfter?: number | string;
            /** Format: int64 */
            estimatedTokensBefore?: number | string;
            lastCompressedAtUtc?: null | string;
            sessionId?: string;
            status?: string;
            /** Format: int64 */
            summaryChars?: number | string;
        };
        ContinueWorkerHttpRequest: {
            prompt?: null | string;
            resumeToken?: null | string;
        };
        ConversationSummaryDto: {
            conversationId: string;
            /** Format: date-time */
            createdUtc: string;
            isArchived: boolean;
            name: string;
            sessionId: null | string;
            taskId: null | string;
            threadType: string;
            /** Format: date-time */
            updatedAtUtc: string;
        };
        CreateBackgroundJobRequest: {
            jobType?: string;
            payloadJson?: null | string;
            sessionId?: string;
            taskId?: null | string;
        };
        CreateBusinessPluginRequest: {
            endpointPolicy: components["schemas"]["EndpointPolicyDto"];
            environment: string;
            name: string;
            sourceType: components["schemas"]["BusinessPluginSourceType"];
            sourceUrl: string;
        };
        CreateConversationRequest: {
            name?: null | string;
        };
        CreateSkillDraftRequest: {
            businessGoal: string;
            catalogSnapshotId: null | string;
            name: string;
            pluginId: string;
            selectedCapabilityIds?: null | string[];
        };
        CredentialRefSummaryDto: {
            authKind?: components["schemas"]["BusinessAuthKind"];
            credentialRef?: string;
            maskedHint?: string;
            revision?: string;
            /** Format: date-time */
            updatedAtUtc?: string;
        };
        DeleteCredentialResponseDto: {
            credentialRef: string;
            deleted: boolean;
            referencedPluginIds: string[];
            /** Format: int32 */
            remainingReferenceCount: number | string;
        };
        DeletePermissionRuleResponse: {
            removed: boolean;
            ruleId: string;
        };
        DeleteResourceResponse: {
            deleted: boolean;
            id: string;
        };
        DisablePluginResponseDto: {
            /** Format: date-time */
            disabledAtUtc: null | string;
            enabled: boolean;
            pluginId: string;
        };
        DiscoverPluginRequest: {
            clientRequestId: string;
        };
        DiscoverPluginResultDto: {
            diff: null | components["schemas"]["CatalogDiffDto"];
            operation: components["schemas"]["PluginOperationAcceptedDto"];
            snapshot: null | components["schemas"]["CatalogSnapshotDto"];
        };
        EndpointPolicyDto: {
            allowedExternalRefOrigins?: string[];
            allowedOrigin?: string;
            allowedPathPrefix?: string;
            allowExternalOpenApiRefs?: boolean;
            allowPlainHttpForIntranet?: boolean;
            customCaThumbprint?: null | string;
        };
        ExecutionSettingsDto: {
            backend: string;
            controlPlaneAvailable: boolean;
            permissionMode: string;
            selfApprovalProtection: boolean;
            tokenDeliveryMode: string;
        };
        GatewayChannelToolOutputSnapshot: {
            commandTaskId: string;
            stderr: null | string;
            stdout: null | string;
            /** Format: int32 */
            suppressedLines: null | number | string;
            throttled: boolean;
            tool: string;
        };
        GatewayChannelToolResultSnapshot: {
            callId: null | string;
            result: string;
            success: boolean;
            toolName: string;
            truncated: boolean;
        };
        GatewayChatRequest: {
            approveGitPush?: boolean;
            attachments?: null | components["schemas"]["ChatAttachmentDto"][];
            branch?: null | string;
            conversationId?: null | string;
            conversationMode?: null | string;
            gitRepoUrl?: null | string;
            message?: string;
            replyId?: null | string;
            sessionId?: string;
            structuredAction?: null | components["schemas"]["GatewayStructuredActionRequest"];
            systemPrompt?: null | string;
            taskId?: null | string;
        };
        GatewayErrorResponse: {
            code: string;
            message: string;
        };
        GatewayStructuredActionRequest: {
            acceptExistingDependencyDebt?: null | boolean;
            action?: string;
            approveDependencyUpgrades?: null | string[];
            idempotencyKey?: null | string;
            planArtifactId?: null | string;
            planHash?: null | string;
            planVersion?: null | string;
            startExecution?: null | boolean;
        };
        GenerateSkillCandidateRequest: {
            clientRequestId: string;
            confirmModelDataScope: boolean;
            /** @default false */
            includeSanitizedSamples: boolean;
        };
        GenerateSkillCandidateResultDto: {
            disclosedDataCategories: string[];
            draft: components["schemas"]["SkillDraftDto"];
            operation: components["schemas"]["PluginOperationAcceptedDto"];
        };
        /** Format: binary */
        IFormFile: string;
        JsonElement: unknown;
        ModelSettingsDto: {
            apiKeyLast4: null | string;
            apiKeySet: boolean;
            apiMode: null | string;
            apiUrl: null | string;
            /** Format: int32 */
            maxTokensLength: null | number | string;
            model: null | string;
            multimodalProbeMessage?: null | string;
            multimodalProbeStatus?: null | string;
            /** @default false */
            supportsMultimodal: boolean;
            /** Format: float */
            temperature: null | number | string;
            /** Format: float */
            topP: null | number | string;
        };
        OpenLocalWorkspaceRequest: {
            name?: null | string;
            path: string;
        };
        OpenLocalWorkspaceResponse: {
            addedToAllowedRoots: boolean;
            created: boolean;
            sessionId: string;
            workspaceName: string;
            workspacePath: string;
        };
        PagedResponseDtoOfConversationSummaryDto: {
            items: components["schemas"]["ConversationSummaryDto"][];
            /** Format: int32 */
            page: number | string;
            /** Format: int32 */
            pageSize: number | string;
            /** Format: int32 */
            total: number | string;
        };
        PagedResponseDtoOfSessionSummaryDto: {
            items: components["schemas"]["SessionSummaryDto"][];
            /** Format: int32 */
            page: number | string;
            /** Format: int32 */
            pageSize: number | string;
            /** Format: int32 */
            total: number | string;
        };
        PendingApprovalItem: {
            allowedDecisions: string[];
            approvalId: string;
            argv: string[];
            backend: string;
            backendNotice?: null | string;
            codeSha256: null | string;
            /** Format: date-time */
            createdAtUtc: string;
            cwd: string;
            executable: string;
            executesWorkspaceCode: boolean;
            /** Format: date-time */
            expiresAtUtc: string;
            filesystem: string;
            inputsManifestSha256: null | string;
            interpreterChain: string[];
            network: string;
            origin?: null | string;
            reason: string;
            requestDigest: string;
            /** Format: int32 */
            scriptLength?: null | number | string;
            scriptPreview?: null | string;
            scriptSha256: null | string;
            sessionId: string;
            shell?: null | string;
            stdinSha256: null | string;
            tool: string;
            workspaceCodeWarning: null | string;
        };
        PendingApprovalScriptSnapshot: {
            approvalId: string;
            /** Format: date-time */
            expiresAtUtc: string;
            requestDigest: string;
            /** Format: int32 */
            scriptLength: number | string;
            scriptSha256: string;
            scriptText: string;
            sessionId: string;
            shell: null | string;
        };
        PendingApprovalsResponse: {
            browserApprovals?: null | components["schemas"]["BrowserApprovalRequestDto"][];
            businessApprovals?: null | components["schemas"]["BusinessApprovalRequestDto"][];
            controlPlaneAvailable: boolean;
            /** Format: int32 */
            count: number | string;
            items: components["schemas"]["PendingApprovalItem"][];
            selfApprovalProtection: boolean;
            sessionId: string;
        };
        PermissionRuleListResponse: {
            /** Format: int32 */
            count: number | string;
            rules: components["schemas"]["PersistedPermissionRule"][];
        };
        PersistedPermissionRule: {
            argv: string[];
            /** Format: date-time */
            createdAtUtc: string;
            executable: string;
            id: string;
            note?: null | string;
            sessionAgnosticDigest: string;
            toolName: string;
        };
        PlanModeResponse: {
            /** Format: int32 */
            modeVersion: number | string;
            planModeActive: boolean;
            /** Format: date-time */
            planModeChangedAtUtc: null | string;
            planModeChangedBy: null | string;
            /** Format: int32 */
            planModeVersion: number | string;
            sessionId: string;
            workMode: string;
        };
        PlanTaskItemDto: {
            dependencies: string[];
            description: string;
            id: string;
            resultNotes: null | string;
            status: string;
            taskType: string;
            title: string;
        };
        PluginConnectionDto: {
            credentialRevision?: string;
            documentCredential?: null | components["schemas"]["CredentialRefSummaryDto"];
            endpointPolicy?: components["schemas"]["EndpointPolicyDto"];
            environment?: string;
            executionCredential?: null | components["schemas"]["CredentialRefSummaryDto"];
            id?: string;
            isEncryptedTransport?: boolean;
            /** Format: int32 */
            revision?: number | string;
            sourceUrl?: string;
        };
        PluginOperationAcceptedDto: {
            kind: string;
            operationId: string;
            reusedFromClientRequestId: boolean;
            /** Format: int64 */
            sequence: number | string;
            status: components["schemas"]["PluginOperationStatus"];
            /** Format: int32 */
            version: number | string;
        };
        /** @enum {unknown} */
        PluginOperationStatus: "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";
        PluginOperationStatusDto: {
            /** Format: date-time */
            createdAtUtc: string;
            error: null | components["schemas"]["BusinessSkillErrorDto"];
            kind: string;
            operationId: string;
            resultMessage: null | string;
            /** Format: int64 */
            sequence: number | string;
            snapshotId: null | string;
            status: components["schemas"]["PluginOperationStatus"];
            targetId: null | string;
            /** Format: date-time */
            updatedAtUtc: string;
            /** Format: int32 */
            version: number | string;
        };
        PreflightIssueDto: {
            component: string;
            message: string;
        };
        ProblemDetails: {
            detail?: null | string;
            instance?: null | string;
            /** Format: int32 */
            status?: null | number | string;
            title?: null | string;
            type?: null | string;
        };
        PublishedSkillDto: {
            /** Format: int32 */
            activeVersion?: number | string;
            description?: string;
            environment?: string;
            id?: string;
            name?: string;
            pluginId?: string;
            versions?: components["schemas"]["PublishedSkillVersionDto"][];
        };
        PublishedSkillVersionDto: {
            catalogSnapshotId: string;
            contentHash: string;
            needsRevalidation: boolean;
            /** Format: date-time */
            publishedAtUtc: string;
            /** Format: int32 */
            version: number | string;
            writeStepVerified: boolean;
        };
        PublishSkillDraftRequest: {
            /** Format: int32 */
            expectedRevision: number | string;
            releaseNote?: null | string;
        };
        PutDraftSelectionRequest: {
            catalogSnapshotId: string;
            /** Format: int32 */
            expectedRevision: number | string;
            selectedCapabilityIds: string[];
        };
        ResumeJobRequest: {
            payloadJson?: null | string;
            resumeToken?: null | string;
        };
        SessionBootstrapResponse: {
            created?: boolean;
            isDefault?: boolean;
            /** Format: int32 */
            modeVersion?: number | string;
            planModeActive?: boolean;
            /** Format: int32 */
            planModeVersion?: number | string;
            sessionId?: string;
            workMode?: string;
            workspaceName?: string;
            workspacePath?: string;
        };
        SessionContextWindowResponse: {
            /** Format: int32 */
            compressionTriggerTokens?: number | string;
            /** Format: int64 */
            estimatedChars?: number | string;
            /** Format: int64 */
            estimatedTokens?: number | string;
            /** Format: int32 */
            modelMaxTokens?: number | string;
            /** Format: int32 */
            recentTurns?: number | string;
            sessionId?: string;
            /** Format: int64 */
            summaryChars?: number | string;
            /** Format: int64 */
            totalCachedPromptTokens?: number | string;
            /** Format: int64 */
            totalCompletionTokens?: number | string;
            /** Format: int64 */
            totalPromptTokens?: number | string;
            /** Format: int64 */
            totalSessionTokens?: number | string;
            /** Format: double */
            triggerRatio?: number | string;
            updatedAtUtc?: null | string;
            /** Format: double */
            usageRatio?: number | string;
        };
        SessionPlanDto: {
            goal: string;
            hostRefusal: null | string;
            markdown: string;
            markdownHash: string;
            planArtifactId: string;
            status: string;
            tasks: components["schemas"]["PlanTaskItemDto"][];
            /** Format: date-time */
            updatedAtUtc: string;
            version: string;
        };
        SessionSnapshotDto: {
            activeConversationId: null | string;
            activeToolOutputs?: null | components["schemas"]["GatewayChannelToolOutputSnapshot"][];
            activeToolResults: components["schemas"]["GatewayChannelToolResultSnapshot"][];
            activeTurn: components["schemas"]["SnapshotActiveTurnDto"];
            /** Format: int64 */
            asOfEventId: number | string;
            channelEpoch: null | string;
            controlPlaneAvailable: boolean;
            currentPlan: null | components["schemas"]["SessionPlanDto"];
            jobs: components["schemas"]["BackgroundJobDto"][];
            messages: components["schemas"]["SnapshotMessageDto"][];
            pendingApprovals: components["schemas"]["PendingApprovalItem"][];
            selfApprovalProtection: boolean;
            session: components["schemas"]["SessionSummaryDto"];
            sessionId: string;
            taskListJson: null | string;
            /** @default false */
            toolOutputsTruncated: boolean;
            /** @default false */
            toolResultsTruncated: boolean;
        };
        SessionSummaryDto: {
            activeTaskId: null | string;
            /** Format: date-time */
            createdUtc: string;
            currentPlanArtifactId: null | string;
            /** Format: int32 */
            currentStage: number | string;
            isArchived: boolean;
            isExternalWorkspace: boolean;
            /**
             * Format: int32
             * @default 1
             */
            modeVersion: number | string;
            name: string;
            /** @default false */
            planModeActive: boolean;
            /**
             * Format: int32
             * @default 1
             */
            planModeVersion: number | string;
            sessionId: string;
            /** @default Development */
            workMode: string;
            workspacePath: string;
        };
        SetPluginCredentialRequest: {
            authKind: components["schemas"]["BusinessAuthKind"];
            secretPayload: string;
        };
        SettingsErrorDto: {
            code: string;
            message: string;
        };
        SkillAggregationMetric: {
            function: string;
            outputField: string;
            sourceField?: null | string;
        };
        SkillBudgetPolicyDto: {
            /** Format: int32 */
            maxExternalRequests?: number | string;
            /** Format: int32 */
            maxJoinOutputRows?: number | string;
            /** Format: int32 */
            maxReadOnlyRetries?: number | string;
            /** Format: int32 */
            maxReadRecords?: number | string;
            /** Format: int32 */
            maxWriteBatchItems?: number | string;
            /** Format: int32 */
            requestTimeoutSeconds?: number | string;
        };
        SkillConditionClause: {
            field: string;
            operator: string;
            value: components["schemas"]["SkillValueBinding"];
        };
        SkillConditionExpr: {
            clauses?: components["schemas"]["SkillConditionClause"][];
            combinator?: string;
        };
        SkillConnectionBinding: {
            connectionId?: string;
            /** Format: int32 */
            connectionRevision?: number | string;
            credentialRevision?: string;
            environment?: string;
            selectedServerUrl?: string;
        };
        SkillDataPolicyDto: {
            allowedModelFields?: string[];
            includeSanitizedSamplesForGeneration?: boolean;
            redactLogsAndExports?: boolean;
        };
        SkillDefinitionV1: {
            budgets?: components["schemas"]["SkillBudgetPolicyDto"];
            catalogSnapshotId?: string;
            connectionBinding?: components["schemas"]["SkillConnectionBinding"];
            contentHash?: string;
            dataPolicy?: components["schemas"]["SkillDataPolicyDto"];
            description?: string;
            inputs?: components["schemas"]["SkillInputSchemaDto"];
            name?: string;
            outputs?: components["schemas"]["SkillOutputSchemaDto"];
            pluginId?: string;
            /** Format: int32 */
            schemaVersion?: number | string;
            selectedCapabilityIds?: string[];
            skillId?: string;
            steps?: components["schemas"]["SkillStepDefinition"][];
            successCriteria?: components["schemas"]["SkillSuccessCriteriaDto"];
            usedCapabilityIds?: string[];
            validationEvidence?: components["schemas"]["SkillValidationEvidenceDto"];
            /** Format: int32 */
            version?: number | string;
        };
        SkillDraftDto: {
            businessGoal?: string;
            candidateDefinition?: null | components["schemas"]["SkillDefinitionV1"];
            catalogSnapshotId?: string;
            clarificationQuestions?: string[];
            id?: string;
            name?: string;
            needsRevalidation?: boolean;
            pluginId?: string;
            /** Format: int32 */
            revision?: number | string;
            selectedCapabilityIds?: string[];
            /** Format: date-time */
            updatedAtUtc?: string;
            validationErrors?: components["schemas"]["BusinessSkillFieldError"][];
        };
        /** @enum {unknown} */
        SkillDuplicateKeyPolicy: "Reject" | "RequirePreAggregation" | "FirstMatchOnly";
        SkillInputPropertyDto: {
            defaultValue?: null | components["schemas"]["JsonElement"];
            description?: null | string;
            enumValues?: null | string[];
            format?: null | string;
            type?: string;
        };
        SkillInputSchemaDto: {
            properties?: {
                [key: string]: components["schemas"]["SkillInputPropertyDto"];
            };
            required?: string[];
            type?: string;
        };
        SkillJoinSpec: {
            duplicateKeyPolicy?: components["schemas"]["SkillDuplicateKeyPolicy"];
            keyType?: string;
            leftKey?: string;
            missingMatchPolicy?: string;
            rightFieldsToInclude?: string[];
            rightKey?: string;
            rightKeyUniqueExpected?: boolean;
            targetPrefix?: null | string;
        };
        SkillMarkdownPreviewDto: {
            contentHash: string;
            markdown: string;
            skillId: string;
            /** Format: int32 */
            version: number | string;
        };
        SkillOutputFieldDto: {
            description?: null | string;
            name: string;
            type: string;
        };
        SkillOutputSchemaDto: {
            fields?: components["schemas"]["SkillOutputFieldDto"][];
            type?: string;
        };
        SkillPaginationConfig: {
            cursorParam?: null | string;
            hasMorePointer?: null | string;
            itemsPointer?: string;
            /** Format: int32 */
            maxPages?: number | string;
            mode?: string;
            nextCursorPointer?: null | string;
            pageParam?: null | string;
            /** Format: int32 */
            pageSize?: number | string;
            pageSizeParam?: null | string;
            totalCountPointer?: null | string;
        };
        SkillProjectionField: {
            currencyField?: null | string;
            dateTimezone?: null | string;
            outputField: string;
            sourcePointer: string;
            /** @default string */
            valueType: string;
        };
        SkillResponseContract: {
            businessCodePointer?: null | string;
            createdIdPointer?: null | string;
            idempotencyKeyTemplate?: null | string;
            statusCheckCapabilityId?: null | string;
            successCodeValues?: string[];
        };
        SkillRunDto: {
            catalogSnapshotId?: string;
            clientRequestId?: string;
            /** Format: date-time */
            createdAtUtc?: string;
            error?: null | components["schemas"]["BusinessSkillErrorDto"];
            /** Format: int32 */
            externalRequestCount?: number | string;
            isTestRun?: boolean;
            outputPayload?: null | components["schemas"]["JsonElement"];
            pendingApproval?: null | components["schemas"]["BusinessApprovalRequestDto"];
            /** Format: int32 */
            readOnlyRetryCount?: number | string;
            runId?: string;
            skillId?: string;
            /** Format: int32 */
            skillVersion?: number | string;
            status?: components["schemas"]["SkillRunStatus"];
            steps?: components["schemas"]["SkillRunStepDto"][];
            stopRequested?: boolean;
            /** Format: date-time */
            updatedAtUtc?: string;
        };
        /** @enum {unknown} */
        SkillRunStatus: "Queued" | "Running" | "WaitingForApproval" | "Succeeded" | "Failed" | "Cancelled" | "PartialSucceeded" | "OutcomeUnknown";
        SkillRunStepDto: {
            /** Format: date-time */
            completedAtUtc?: null | string;
            errorCode?: null | string;
            evidence?: null | string;
            requestSummary?: null | string;
            responseSummary?: null | string;
            /** Format: date-time */
            startedAtUtc?: string;
            state?: components["schemas"]["SkillRunStepState"];
            stepId?: string;
            stepType?: string;
        };
        /** @enum {unknown} */
        SkillRunStepState: "Prepared" | "Dispatching" | "Succeeded" | "Failed" | "Cancelled" | "OutcomeUnknown";
        SkillSortSpec: {
            /** @default false */
            descending: boolean;
            field: string;
        };
        SkillStepDefinition: components["schemas"]["SkillStepDefinitionSkillToolStep"] | components["schemas"]["SkillStepDefinitionSkillTransformStep"] | components["schemas"]["SkillStepDefinitionSkillBranchStep"] | components["schemas"]["SkillStepDefinitionSkillForeachStep"] | components["schemas"]["SkillStepDefinitionSkillGenerateTextStep"] | components["schemas"]["SkillStepDefinitionSkillApprovalStep"] | components["schemas"]["SkillStepDefinitionSkillOutputStep"];
        SkillStepDefinitionSkillApprovalStep: {
            currencyField?: null | string;
            id?: string;
            itemsSource?: components["schemas"]["SkillValueBinding"];
            keyAmountField?: null | string;
            plannedMutation?: string;
            targetCapabilityId?: string;
            targetRecordFields?: string[];
            title?: null | string;
            /** @enum {string} */
            type?: "approval";
        };
        SkillStepDefinitionSkillBranchStep: {
            condition?: components["schemas"]["SkillConditionExpr"];
            elseStepIds?: string[];
            id?: string;
            thenStepIds?: string[];
            title?: null | string;
            /** @enum {string} */
            type?: "branch";
        };
        SkillStepDefinitionSkillForeachStep: {
            bodyStepIds?: string[];
            id?: string;
            itemVariable?: string;
            /** Format: int32 */
            maxItems?: number | string;
            sequential?: boolean;
            source?: components["schemas"]["SkillValueBinding"];
            title?: null | string;
            /** @enum {string} */
            type?: "foreach";
        };
        SkillStepDefinitionSkillGenerateTextStep: {
            allowedInputFields?: string[];
            id?: string;
            promptTemplate?: string;
            source?: components["schemas"]["SkillValueBinding"];
            title?: null | string;
            /** @enum {string} */
            type?: "generateText";
        };
        SkillStepDefinitionSkillOutputStep: {
            id?: string;
            namedOutputs?: {
                [key: string]: components["schemas"]["SkillValueBinding"];
            };
            source?: components["schemas"]["SkillValueBinding"];
            title?: null | string;
            /** @enum {string} */
            type?: "output";
        };
        SkillStepDefinitionSkillToolStep: {
            arguments?: {
                [key: string]: components["schemas"]["SkillValueBinding"];
            };
            capabilityId?: string;
            id?: string;
            pagination?: null | components["schemas"]["SkillPaginationConfig"];
            responseContract?: null | components["schemas"]["SkillResponseContract"];
            title?: null | string;
            /** @enum {string} */
            type?: "tool";
        };
        SkillStepDefinitionSkillTransformStep: {
            aggregations?: components["schemas"]["SkillAggregationMetric"][];
            groupByFields?: string[];
            id?: string;
            joinSpec?: null | components["schemas"]["SkillJoinSpec"];
            operation?: components["schemas"]["SkillTransformOperation"];
            /** Format: int32 */
            outputLimit?: number | string;
            predicate?: null | components["schemas"]["SkillConditionExpr"];
            projections?: components["schemas"]["SkillProjectionField"][];
            secondarySource?: null | components["schemas"]["SkillValueBinding"];
            sortBy?: components["schemas"]["SkillSortSpec"][];
            source?: components["schemas"]["SkillValueBinding"];
            title?: null | string;
            /** @enum {string} */
            type?: "transform";
        };
        SkillSuccessCriteriaDto: {
            failOnPartialData?: boolean;
            requireCompletePagination?: boolean;
            requiredSteps?: string[];
        };
        /** @enum {unknown} */
        SkillTransformOperation: "Filter" | "Sort" | "Project" | "Aggregate" | "InnerJoin" | "LeftJoin" | "Lookup";
        SkillValidationEvidenceDto: {
            catalogHash?: string;
            /** Format: int32 */
            draftRevision?: number | string;
            readOnlyTestRunEvidenceHash?: null | string;
            readOnlyTestRunId?: null | string;
            staticValidationHash?: string;
            /** Format: date-time */
            verifiedAtUtc?: null | string;
            writeStepVerified?: boolean;
        };
        SkillValueBinding: {
            input?: null | string;
            item?: null | string;
            literal?: null | components["schemas"]["JsonElement"];
            pointer?: null | string;
            step?: null | string;
        };
        SnapshotActiveTurnDto: {
            /** @default false */
            contentTruncated: boolean;
            conversationId: null | string;
            isRunning: boolean;
            messageId: null | string;
            partialContent?: null | string;
            partialThinking?: null | string;
        };
        SnapshotMessageDto: {
            content: string;
            /** Format: date-time */
            createdUtc: string;
            id: string;
            messageType: null | string;
            notificationMeta: null | string;
            role: string;
            thinkingContent: null | string;
            toolCallResult: null | string;
            truncated: boolean;
        };
        StartSkillRunRequest: {
            /** @default false */
            allowWriteInTestRun: boolean;
            clientRequestId: string;
            inputs: Record<string, never>;
            /** @default false */
            isTestRun: boolean;
            sessionId?: null | string;
            /** Format: int32 */
            version: null | number | string;
        };
        SubmitExecutionApprovalRequest: {
            decision: string;
            note?: null | string;
            requestDigest: string;
        };
        SystemStatusSummaryDto: {
            backendAvailabilityReason?: null | string;
            /** @default true */
            backendAvailable: boolean;
            controlPlaneAvailable: boolean;
            dockerAvailabilityReason?: null | string;
            /** @default false */
            dockerAvailable: boolean;
            executionBackend: string;
            /** @default false */
            modelConfigured: boolean;
            /** @default Unknown */
            modelProbeStatus: string;
            /** @default false */
            modelReachable: null | boolean;
            permissionMode: string;
            preflightIssues?: null | components["schemas"]["PreflightIssueDto"][];
            /** @default Fail */
            preflightMode: string;
            selfApprovalProtection: boolean;
            tokenDeliveryMode: string;
            version: string;
        };
        UpdateBrowserSettingsRequest: {
            allowControlledDownloads?: null | boolean;
            allowedDomains?: null | string[];
            allowLocalhostInDailyMode?: null | boolean;
            blockedDomains?: null | string[];
            developmentAllowedLoopbackPorts?: null | (number | string)[];
            enabled?: null | boolean;
            executablePath?: null | string;
            headless?: null | boolean;
            /** Format: int32 */
            idleTimeoutMinutes?: null | number | string;
            interactionApprovalPolicy?: null | string;
            /** Format: int32 */
            maxConcurrentSessions?: null | number | string;
            profileMode?: null | string;
            remoteCdpWsUrl?: null | string;
        };
        UpdateCapabilityRiskRequest: {
            capabilityId: string;
            note?: null | string;
            risk: components["schemas"]["CapabilityRiskLevel"];
        };
        UpdateConversationRequest: {
            isArchived?: null | boolean;
            name?: null | string;
        };
        UpdateExecutionSettingsRequest: {
            backend?: null | string;
            permissionMode?: null | string;
        };
        UpdateModelSettingsRequest: {
            apiKey?: null | string;
            apiMode?: null | string;
            apiUrl?: null | string;
            /** Format: int32 */
            maxTokensLength?: null | number | string;
            model?: null | string;
            supportsMultimodal?: null | boolean;
            /** Format: float */
            temperature?: null | number | string;
            /** Format: float */
            topP?: null | number | string;
        };
        UpdatePlanModeRequest: {
            active: boolean;
            /** Format: int32 */
            expectedPlanModeVersion?: null | number | string;
            objective?: null | string;
            reason?: null | string;
        };
        UpdatePluginConnectionRequest: {
            endpointPolicy: components["schemas"]["EndpointPolicyDto"];
            environment: string;
            /** Format: int32 */
            expectedRevision: number | string;
            sourceUrl: string;
        };
        UpdateSessionRequest: {
            isArchived?: null | boolean;
            mode?: null | string;
            name?: null | string;
            workMode?: null | string;
        };
        UpdateSkillDraftRequest: {
            businessGoal?: null | string;
            candidateDefinition?: null | components["schemas"]["SkillDefinitionV1"];
            /** Format: int32 */
            expectedRevision: number | string;
            name?: null | string;
        };
        UploadedAttachmentDto: {
            contentType: string;
            name: string;
            relativePath: string;
            /** Format: int64 */
            sizeBytes: number | string;
        };
        VerifyForgeArtifactRequest: {
            jobId: string;
            sessionId: string;
        };
        WorkspaceChangedFileDto: {
            /** Format: int32 */
            additions: number | string;
            /** Format: int32 */
            deletions: number | string;
            filePath: string;
            status: string;
        };
        WorkspaceChangesDto: {
            baseRef: null | string;
            files: components["schemas"]["WorkspaceChangedFileDto"][];
            hasGitRepository: boolean;
            sessionId: string;
            truncated: boolean;
            unifiedDiff: string;
        };
        WorkspaceDirectoryListingDto: {
            entries: components["schemas"]["WorkspaceFileEntryDto"][];
            path: string;
            sessionId: string;
            truncated: boolean;
        };
        WorkspaceFileContentDto: {
            content: string;
            encoding: string;
            isBinary: boolean;
            path: string;
            sessionId: string;
            /** Format: int64 */
            sizeBytes: number | string;
            truncated: boolean;
        };
        WorkspaceFileEntryDto: {
            isDirectory: boolean;
            /** Format: date-time */
            lastModifiedUtc: string;
            name: string;
            path: string;
            /** Format: int64 */
            sizeBytes: number | string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
};
export type $defs = Record<string, never>;
export type operations = Record<string, never>;

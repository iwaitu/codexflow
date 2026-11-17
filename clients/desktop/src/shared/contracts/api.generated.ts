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
        CancelTurnResponse: {
            cancelled: boolean;
            sessionId: string;
            status: string;
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
        CreateConversationRequest: {
            name?: null | string;
        };
        DeletePermissionRuleResponse: {
            removed: boolean;
            ruleId: string;
        };
        DeleteResourceResponse: {
            deleted: boolean;
            id: string;
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
        /** Format: binary */
        IFormFile: string;
        ModelSettingsDto: {
            apiKeyLast4: null | string;
            apiKeySet: boolean;
            apiMode: null | string;
            apiUrl: null | string;
            /** Format: int32 */
            maxTokensLength: null | number | string;
            model: null | string;
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
            reason: string;
            requestDigest: string;
            scriptSha256: null | string;
            sessionId: string;
            stdinSha256: null | string;
            tool: string;
            workspaceCodeWarning: null | string;
        };
        PendingApprovalsResponse: {
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
        PlanTaskItemDto: {
            dependencies: string[];
            description: string;
            id: string;
            resultNotes: null | string;
            status: string;
            taskType: string;
            title: string;
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
        ResumeJobRequest: {
            payloadJson?: null | string;
            resumeToken?: null | string;
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
            name: string;
            sessionId: string;
            workspacePath: string;
        };
        SettingsErrorDto: {
            code: string;
            message: string;
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
            /** Format: float */
            temperature?: null | number | string;
            /** Format: float */
            topP?: null | number | string;
        };
        UpdateSessionRequest: {
            isArchived?: null | boolean;
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
